// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.NuCache.InMemory;
using BuildXL.Cache.ContentStore.Distributed.Utilities;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.MemoizationStore.Interfaces.Results;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Tasks;
using BuildXL.Utilities.Tracing;
using static BuildXL.Cache.ContentStore.Distributed.Tracing.TracingStructuredExtensions;
using AbsolutePath = BuildXL.Cache.ContentStore.Interfaces.FileSystem.AbsolutePath;

namespace BuildXL.Cache.ContentStore.Distributed.NuCache
{
    /// <summary>
    /// Base class that implements the core logic of <see cref="ContentLocationDatabase"/> interface.
    /// </summary>
    public abstract class ContentLocationDatabase : StartupShutdownSlimBase
    {
        private readonly ObjectPool<StreamBinaryWriter> _writerPool = new ObjectPool<StreamBinaryWriter>(() => new StreamBinaryWriter(), w => w.ResetPosition());
        private readonly ObjectPool<StreamBinaryReader> _readerPool = new ObjectPool<StreamBinaryReader>(() => new StreamBinaryReader(), r => { });

        /// <nodoc />
        protected readonly IClock Clock;

        /// <nodoc />
        protected override Tracer Tracer { get; } = new Tracer(nameof(ContentLocationDatabase)) { LogOperationStarted = false };

        /// <nodoc />
        public CounterCollection<ContentLocationDatabaseCounters> Counters { get; } = new CounterCollection<ContentLocationDatabaseCounters>();

        private readonly Func<IReadOnlyList<MachineId>> _getInactiveMachines;

        private Timer _gcTimer;
        private NagleQueue<(ShortHash hash, EntryOperation op, OperationReason reason)> _nagleOperationTracer;
        private readonly ContentLocationDatabaseConfiguration _configuration;

        /// <nodoc />
        protected bool IsDatabaseWriteable;
        private bool _isContentGarbageCollectionEnabled;
        private bool _isMetadataGarbageCollectionEnabled;

        /// <nodoc />
        private bool IsGarbageCollectionEnabled => _isContentGarbageCollectionEnabled || _isMetadataGarbageCollectionEnabled;

        /// <summary>
        /// Fine-grained locks that is used for all operations that mutate records.
        /// </summary>
        private readonly object[] _locks = Enumerable.Range(0, ushort.MaxValue + 1).Select(s => new object()).ToArray();

        /// <summary>
        /// Whether the cache is currently being used. Can only possibly be true in master. Only meant for testing
        /// purposes.
        /// </summary>
        internal bool IsInMemoryCacheEnabled { get; private set; } = false;

        private readonly FlushableCache _inMemoryCache;

        /// <summary>
        /// This counter is not exact, but provides an approximate count. It may be thwarted by flushes and cache
        /// activate/deactivate events. Its only purpose is to roughly help ensure flushes are more frequent as
        /// more operations are performed.
        /// </summary>
        private long _cacheUpdatesSinceLastFlush = 0;

        /// <summary>
        /// External users should be tests only
        /// </summary>
        internal long CacheUpdatesSinceLastFlush => _cacheUpdatesSinceLastFlush;

        /// <summary>
        /// Controls cache flushing due to timeout.
        /// </summary>
        private Timer _inMemoryCacheFlushTimer;

        /// <nodoc />
        protected readonly object TimerChangeLock = new object();

        private readonly object _cacheFlushTimerLock = new object();


        private readonly object _flushTaskLock = new object();

        /// <summary>
        /// Currently ongoing flush
        ///
        /// External users should be tests only
        /// </summary>
        internal Task FlushTask { get; private set; } = BoolResult.SuccessTask;

        /// <summary>
        /// Event callback that's triggered when the database is permanently invalidated. 
        /// </summary>
        public Action<OperationContext, Failure<Exception>> DatabaseInvalidated;

        /// <nodoc />
        protected void OnDatabaseInvalidated(OperationContext context, Failure<Exception> failure)
        {
            Contract.Requires(failure != null);

            // Notice that no update to the internal state is required when invalidation happens. By definition,
            // nothing can be done to this instance after invalidation: all incoming and ongoing operations should fail
            // (because it is triggered by RocksDb). The only way to resume operation is to reload from a checkpoint,
            // which resets the internal state correctly.
            DatabaseInvalidated?.Invoke(context, failure);
        }

        /// <nodoc />
        protected ContentLocationDatabase(IClock clock, ContentLocationDatabaseConfiguration configuration, Func<IReadOnlyList<MachineId>> getInactiveMachines)
        {
            Contract.Requires(clock != null);
            Contract.Requires(configuration != null);
            Contract.Requires(getInactiveMachines != null);

            Clock = clock;
            _configuration = configuration;
            _getInactiveMachines = getInactiveMachines;

            _inMemoryCache = new FlushableCache(configuration, this);

            _isMetadataGarbageCollectionEnabled = configuration.MetadataGarbageCollectionEnabled;
        }

        /// <summary>
        /// Sets a key to a given value in the global info map
        /// </summary>
        public abstract void SetGlobalEntry(string key, string value);

        /// <summary>
        /// Attempts to get a value from the global info map
        /// </summary>
        public abstract bool TryGetGlobalEntry(string key, out string value); 

        /// <summary>
        /// Factory method that creates an instance of a <see cref="ContentLocationDatabase"/> based on an optional <paramref name="configuration"/> instance.
        /// </summary>
        public static ContentLocationDatabase Create(IClock clock, ContentLocationDatabaseConfiguration configuration, Func<IReadOnlyList<MachineId>> getInactiveMachines)
        {
            Contract.Requires(clock != null);
            Contract.Requires(configuration != null);

            switch (configuration)
            {
                case MemoryContentLocationDatabaseConfiguration memoryConfiguration:
                    return new MemoryContentLocationDatabase(clock, memoryConfiguration, getInactiveMachines);
                case RocksDbContentLocationDatabaseConfiguration rocksDbConfiguration:
                    return new RocksDbContentLocationDatabase(clock, rocksDbConfiguration, getInactiveMachines);
                default:
                    throw new InvalidOperationException($"Unknown configuration instance of type '{configuration.GetType()}'");
            }
        }

        /// <summary>
        /// Prepares the database for read only or read/write mode. This operation assumes no operations are underway
        /// while running. It is the responsibility of the caller to ensure that is so.
        /// </summary>
        public virtual void SetDatabaseMode(bool isDatabaseWriteable)
        {
            // The parameter indicates whether we will be in writeable state or not after this function runs. The
            // following calls can see if we transition from read/only to read/write by looking at the internal value
            ConfigureGarbageCollection(isDatabaseWriteable);
            ConfigureInMemoryDatabaseCache(isDatabaseWriteable);

            IsDatabaseWriteable = isDatabaseWriteable;
        }

        /// <summary>	
        /// Configures the behavior of the database's garbage collection	
        /// </summary>	
        private void ConfigureGarbageCollection(bool isDatabaseWriteable)
        {
            if (IsDatabaseWriteable != isDatabaseWriteable)
            {
                _isContentGarbageCollectionEnabled = isDatabaseWriteable;
                _isMetadataGarbageCollectionEnabled = isDatabaseWriteable && _configuration.MetadataGarbageCollectionEnabled;

                var nextGcTimeSpan = IsGarbageCollectionEnabled ? _configuration.GarbageCollectionInterval : Timeout.InfiniteTimeSpan;
                _gcTimer?.Change(nextGcTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        private void ConfigureInMemoryDatabaseCache(bool isDatabaseWritable)
        {
            if (_configuration.ContentCacheEnabled)
            {
                // This clear is actually safe, as no operations should happen concurrently with this function.
                _inMemoryCache.UnsafeClear();

                Interlocked.Exchange(ref _cacheUpdatesSinceLastFlush, 0);

                lock (_cacheFlushTimerLock)
                {
                    IsInMemoryCacheEnabled = isDatabaseWritable;
                }

                ResetFlushTimer();
            }
        }

        private void ResetFlushTimer()
        {
            lock (_cacheFlushTimerLock)
            {
                var cacheFlushTimeSpan = IsInMemoryCacheEnabled
                    ? _configuration.CacheFlushingMaximumInterval
                    : Timeout.InfiniteTimeSpan;

                _inMemoryCacheFlushTimer?.Change(cacheFlushTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        /// <inheritdoc />
        protected override Task<BoolResult> StartupCoreAsync(OperationContext context)
        {
            if (_configuration.GarbageCollectionInterval != Timeout.InfiniteTimeSpan)
            {
                _gcTimer = new Timer(
                    _ => GarbageCollect(context),
                    null,
                    IsGarbageCollectionEnabled ? _configuration.GarbageCollectionInterval : Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan);
            }

            if (_configuration.ContentCacheEnabled && _configuration.CacheFlushingMaximumInterval != Timeout.InfiniteTimeSpan)
            {
                _inMemoryCacheFlushTimer = new Timer(
                    _ => {
                        ForceCacheFlush(context,
                            counter: ContentLocationDatabaseCounters.NumberOfCacheFlushesTriggeredByTimer,
                            blocking: false);
                    },
                    null,
                    Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan);
            }

            _nagleOperationTracer = NagleQueue<(ShortHash, EntryOperation, OperationReason)>.Create(
                ops =>
                {
                    LogContentLocationOperations(context, Tracer.Name, ops);
                    return Unit.VoidTask;
                },
                maxDegreeOfParallelism: 1,
                interval: TimeSpan.FromMinutes(1),
                batchSize: 100);

            return Task.FromResult(InitializeCore(context));
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> ShutdownCoreAsync(OperationContext context)
        {
            _nagleOperationTracer?.Dispose();

            lock (TimerChangeLock)
            {
#pragma warning disable AsyncFixer02
                _gcTimer?.Dispose();
#pragma warning restore AsyncFixer02

                _gcTimer = null;
            }

            lock (_cacheFlushTimerLock)
            {
#pragma warning disable AsyncFixer02
                _inMemoryCacheFlushTimer?.Dispose();
#pragma warning restore AsyncFixer02

                _inMemoryCacheFlushTimer = null;
            }

            // NOTE(jubayard): there could be a flush in progress as this is done. Either way, any writes performed
            // after the last checkpoint will be completely lost. Since no checkpoints will be created after this runs,
            // it doesn't make any sense to flush here. However, we can't close the DB or anything like that until this
            // flush is over.
            await FlushTask;

            return await base.ShutdownCoreAsync(context);
        }

        /// <nodoc />
        protected abstract BoolResult InitializeCore(OperationContext context);

        /// <summary>
        /// Tries to locate an entry for a given hash.
        /// </summary>
        public bool TryGetEntry(OperationContext context, ShortHash hash, out ContentLocationEntry entry)
        {
            if (TryGetEntryCore(context, hash, out entry))
            {
                entry = FilterInactiveMachines(entry);
                return true;
            }

            return false;
        }

        /// <nodoc />
        protected abstract IEnumerable<ShortHash> EnumerateSortedKeysFromStorage(CancellationToken token);

        /// <summary>
        /// Gets a sequence of keys.
        /// </summary>
        protected IEnumerable<ShortHash> EnumerateSortedKeys(OperationContext context)
        {
            // NOTE: This is used by GC which will query for the value itself and thereby
            // get the value from the in memory cache if present. It will NOT necessarily
            // enumerate all keys in the in memory cache since they may be new keys but GC
            // is fine to just handle those on the next GC iteration
            return EnumerateSortedKeysFromStorage(context.Token);
        }

        /// <summary>
        /// Enumeration filter used by <see cref="ContentLocationDatabase.EnumerateEntriesWithSortedKeys"/> to filter out entries by raw value from a database.
        /// </summary>
        public class EnumerationFilter
        {
            /// <nodoc />
            public Func<byte[], bool> ShouldEnumerate { get; set; }

            /// <nodoc />
            public ShortHash? StartingPoint { get; set; }
        }

        /// <nodoc />
        protected abstract IEnumerable<(ShortHash key, ContentLocationEntry entry)> EnumerateEntriesWithSortedKeysFromStorage(
            CancellationToken token,
            EnumerationFilter filter = null);

        /// <summary>
        /// Gets a sequence of keys and values sorted by keys.
        /// </summary>
        public IEnumerable<(ShortHash key, ContentLocationEntry entry)> EnumerateEntriesWithSortedKeys(
            OperationContext context,
            EnumerationFilter filter = null)
        {
            // Flush only when the database is writable (and the cache is enabled).
            if (IsDatabaseWriteable && IsInMemoryCacheEnabled)
            {
                ForceCacheFlush(context, ContentLocationDatabaseCounters.NumberOfCacheFlushesTriggeredByContentEnumeration, blocking: true);
            }

            return EnumerateEntriesWithSortedKeysFromStorage(context.Token, filter);
        }

        /// <summary>
        /// Collects entries with last access time longer then time to live.
        /// </summary>
        public void GarbageCollect(OperationContext context)
        {
            if (ShutdownStarted)
            {
                return;
            }

            context.PerformOperation(Tracer,
                () =>
                {
                    using (var cancellableContext = TrackShutdown(context.CreateNested()))
                    {
                        if (_isMetadataGarbageCollectionEnabled)
                        {
                            // Metadata GC could remove content, and hence runs first in order to avoid extra work later on
                            var metadataGcResult = GarbageCollectMetadata(cancellableContext);
                            if (!metadataGcResult.Succeeded)
                            {
                                return metadataGcResult;
                            }
                        }

                        if (_isContentGarbageCollectionEnabled)
                        {
                            var contentGcResult = GarbageCollectContent(cancellableContext);
                            if (!contentGcResult.Succeeded)
                            {
                                return contentGcResult;
                            }
                        }
                    }

                    return BoolResult.Success;
                }, counter: Counters[ContentLocationDatabaseCounters.GarbageCollect]).IgnoreFailure();

            if (!ShutdownStarted)
            {
                lock (TimerChangeLock)
                {
                    _gcTimer?.Change(_configuration.GarbageCollectionInterval, Timeout.InfiniteTimeSpan);
                }
            }
        }

        /// <summary>
        /// Collect unreachable entries from the local database.
        /// </summary>
        private BoolResult GarbageCollectContent(OperationContext context)
        {
            return context.PerformOperation(Tracer,
                () => GarbageCollectContentCore(context),
                counter: Counters[ContentLocationDatabaseCounters.GarbageCollectContent]);
        }

        // Iterate over all content in DB, for each hash removing locations known to
        // be inactive, and removing hashes with no locations.
        private BoolResult GarbageCollectContentCore(OperationContext context)
        {
            // Counters for work done.
            int removedEntries = 0;
            int totalEntries = 0;
            long uniqueContentSize = 0;
            long totalContentCount = 0;
            long totalContentSize = 0;
            int uniqueContentCount = 0;

            // Tracking the difference between sequence of hashes for diagnostic purposes. We need to know how good short hashes are and how close are we to collisions. 
            ShortHash? lastHash = null;
            int maxHashFirstByteDifference = 0;

            long[] totalSizeByLogSize = new long[64];
            long[] uniqueSizeByLogSize = new long[totalSizeByLogSize.Length];
            int[] countsByLogSize = new int[totalSizeByLogSize.Length];

            // Enumerate over all hashes...
            foreach (var hash in EnumerateSortedKeys(context))
            {
                if (context.Token.IsCancellationRequested)
                {
                    break;
                }

                if (!TryGetEntryCore(context, hash, out var entry))
                {
                    continue;
                }

                // Update counters.
                int replicaCount = entry.Locations.Count;
                uniqueContentCount++;
                uniqueContentSize += entry.ContentSize;
                totalContentSize += entry.ContentSize * replicaCount;
                totalContentCount += replicaCount;

                int logSize = (int)Math.Log(Math.Max(1, entry.ContentSize), 2);
                countsByLogSize[logSize]++;
                totalSizeByLogSize[logSize] += entry.ContentSize * replicaCount;
                uniqueSizeByLogSize[logSize] += entry.ContentSize;

                // Filter out inactive machines.
                var filteredEntry = FilterInactiveMachines(entry);

                // Decide if we ought to modify the entry.
                if (filteredEntry.Locations.Count == 0 || filteredEntry.Locations.Count != entry.Locations.Count)
                {
                    // Use double-checked locking to usually avoid locking, but still
                    // be safe in case we are in a race to update content location data.
                    lock (GetLock(hash))
                    {
                        if (!TryGetEntryCore(context, hash, out entry))
                        {
                            continue;
                        }
                        filteredEntry = FilterInactiveMachines(entry);

                        if (filteredEntry.Locations.Count == 0)
                        {
                            // If there are no good locations, remove the entry.
                            removedEntries++;
                            Counters[ContentLocationDatabaseCounters.TotalNumberOfCollectedEntries].Increment();
                            Delete(context, hash);
                            LogEntryDeletion(hash, OperationReason.GarbageCollect, entry.ContentSize);
                        }
                        else if(filteredEntry.Locations.Count != entry.Locations.Count)
                        {
                            // If there are some bad locations, remove them.
                            Counters[ContentLocationDatabaseCounters.TotalNumberOfCleanedEntries].Increment();
                            Store(context, hash, filteredEntry);
                            _nagleOperationTracer.Enqueue((hash, EntryOperation.RemoveMachine, OperationReason.GarbageCollect));
                        }
                    }
                }

                totalEntries++;

                // Some logic to try to measure how "close" short hashes get.
                // dawright: I don't think this works, because hashes could be very close (e.g. all same in low-order bits)
                // and yet still be very far away when ordered (e.g. high-order bits differ), and we only compare
                // neighbors in ordered list. But I'm leaving it for now because it's orthogonal to my current change.
                if (lastHash != null && lastHash != hash)
                {
                    maxHashFirstByteDifference = Math.Max(maxHashFirstByteDifference, GetFirstByteDifference(lastHash.Value, hash));
                }
                lastHash = hash;
            }

            Counters[ContentLocationDatabaseCounters.TotalNumberOfScannedEntries].Add(uniqueContentCount);

            Tracer.Debug(context, $"Overall DB Stats: UniqueContentCount={uniqueContentCount}, UniqueContentSize={uniqueContentSize}, "
                + $"TotalContentCount={totalContentCount}, TotalContentSize={totalContentSize}, MaxHashFirstByteDifference={maxHashFirstByteDifference}" 
                + $", UniqueContentAddedSize={Counters[ContentLocationDatabaseCounters.UniqueContentAddedSize].Value}"
                + $", TotalNumberOfCreatedEntries={Counters[ContentLocationDatabaseCounters.TotalNumberOfCreatedEntries].Value}"
                + $", TotalContentAddedSize={Counters[ContentLocationDatabaseCounters.TotalContentAddedSize].Value}"
                + $", TotalContentAddedCount={Counters[ContentLocationDatabaseCounters.TotalContentAddedCount].Value}"
                + $", UniqueContentRemovedSize={Counters[ContentLocationDatabaseCounters.UniqueContentRemovedSize].Value}"
                + $", TotalNumberOfDeletedEntries={Counters[ContentLocationDatabaseCounters.TotalNumberOfDeletedEntries].Value}"
                + $", TotalContentRemovedSize={Counters[ContentLocationDatabaseCounters.TotalContentRemovedSize].Value}"
                + $", TotalContentRemovedCount={Counters[ContentLocationDatabaseCounters.TotalContentRemovedCount].Value}"
                );

            for (int logSize = 0; logSize < countsByLogSize.Length; logSize++)
            {
                if (countsByLogSize[logSize] != 0)
                {
                    Tracer.Debug(context, $"DB Content Stat: Log2_Size={logSize}, Count={countsByLogSize[logSize]}, UniqueSize={uniqueSizeByLogSize[logSize]}, TotalSize={totalSizeByLogSize[logSize]}, IsComplete={!context.Token.IsCancellationRequested}");
                }
            }

            Tracer.GarbageCollectionFinished(
                context,
                Counters[ContentLocationDatabaseCounters.GarbageCollectContent].Duration,
                totalEntries,
                removedEntries,
                Counters[ContentLocationDatabaseCounters.TotalNumberOfCollectedEntries].Value,
                uniqueContentCount,
                uniqueContentSize,
                totalContentCount,
                totalContentSize);

            return BoolResult.Success;
        }

        /// <summary>
        /// Perform garbage collection of metadata entries.
        /// </summary>
        private BoolResult GarbageCollectMetadata(OperationContext context)
        {
            return context.PerformOperation(Tracer,
                () => GarbageCollectMetadataCore(context),
                counter: Counters[ContentLocationDatabaseCounters.GarbageCollectMetadata]);
        }

        /// <nodoc />
        protected abstract BoolResult GarbageCollectMetadataCore(OperationContext context);

        private int GetFirstByteDifference(in ShortHash hash1, in ShortHash hash2)
        {
            for (int i = 0; i < ShortHash.SerializedLength; i++)
            {
                if (hash1[i] != hash2[i])
                {
                    return i;
                }
            }

            return ShortHash.SerializedLength;
        }

        private ContentLocationEntry FilterInactiveMachines(ContentLocationEntry entry)
        {
            var inactiveMachines = _getInactiveMachines();
            return entry.SetMachineExistence(inactiveMachines, exists: false);
        }

        /// <summary>
        /// Synchronizes machine location data between the database and the given cluster state instance
        /// </summary>
        public void UpdateClusterState(OperationContext context, ClusterState clusterState, bool write)
        {
            if (!_configuration.StoreClusterState)
            {
                return;
            }

            context.PerformOperation(
                Tracer,
                () =>
                {
                    // TODO: Handle setting inactive machines here
                    UpdateClusterStateCore(context, clusterState, write);

                    return BoolResult.Success;
                }).IgnoreFailure();
        }

        /// <nodoc />
        protected abstract void UpdateClusterStateCore(OperationContext context, ClusterState clusterState, bool write);

        /// <summary>
        /// Gets whether the file in the database's checkpoint directory is immutable between checkpoints (i.e. files with the same name will have the same content)
        /// </summary>
        public abstract bool IsImmutable(AbsolutePath dbFile);

        /// <nodoc/>
        public BoolResult SaveCheckpoint(OperationContext context, AbsolutePath checkpointDirectory)
        {
            using (Counters[ContentLocationDatabaseCounters.SaveCheckpoint].Start())
            {
                if (IsInMemoryCacheEnabled)
                {
                    ForceCacheFlush(context,
                        counter: ContentLocationDatabaseCounters.NumberOfCacheFlushesTriggeredByCheckpoint,
                        blocking: true);
                }

                return context.PerformOperation(Tracer,
                    () => SaveCheckpointCore(context, checkpointDirectory),
                    extraStartMessage: $"CheckpointDirectory=[{checkpointDirectory}]",
                    messageFactory: _ => $"CheckpointDirectory=[{checkpointDirectory}]");
            }
        }

        /// <nodoc />
        protected abstract BoolResult SaveCheckpointCore(OperationContext context, AbsolutePath checkpointDirectory);

        /// <nodoc/>
        public BoolResult RestoreCheckpoint(OperationContext context, AbsolutePath checkpointDirectory)
        {
            using (Counters[ContentLocationDatabaseCounters.RestoreCheckpoint].Start())
            {
                return context.PerformOperation(Tracer,
                    () => RestoreCheckpointCore(context, checkpointDirectory),
                    extraStartMessage: $"CheckpointDirectory=[{checkpointDirectory}]",
                    messageFactory: _ => $"CheckpointDirectory=[{checkpointDirectory}]");
            }
        }

        /// <nodoc />
        protected abstract BoolResult RestoreCheckpointCore(OperationContext context, AbsolutePath checkpointDirectory);

        /// <nodoc />
        protected abstract bool TryGetEntryCoreFromStorage(OperationContext context, ShortHash hash, out ContentLocationEntry entry);

        /// <nodoc />
        protected bool TryGetEntryCore(OperationContext context, ShortHash hash, out ContentLocationEntry entry)
        {
            Counters[ContentLocationDatabaseCounters.NumberOfGetOperations].Increment();

            if (IsInMemoryCacheEnabled && _inMemoryCache.TryGetEntry(hash, out entry))
            {
                return true;
            }

            return TryGetEntryCoreFromStorage(context, hash, out entry);
        }

        /// <nodoc />
        internal abstract void Persist(OperationContext context, ShortHash hash, ContentLocationEntry entry);

        /// <nodoc />
        internal virtual void PersistBatch(OperationContext context, IEnumerable<KeyValuePair<ShortHash, ContentLocationEntry>> pairs)
        {
            foreach (var pair in pairs)
            {
                Persist(context, pair.Key, pair.Value);
            }
        }

        /// <nodoc />
        public void Store(OperationContext context, ShortHash hash, ContentLocationEntry entry)
        {
            Counters[ContentLocationDatabaseCounters.NumberOfStoreOperations].Increment();

            if (IsInMemoryCacheEnabled)
            {
                _inMemoryCache.Store(context, hash, entry);

                var updates = Interlocked.Increment(ref _cacheUpdatesSinceLastFlush);
                if (_configuration.CacheMaximumUpdatesPerFlush > 0 && updates >= _configuration.CacheMaximumUpdatesPerFlush && FlushTask.IsCompleted)
                {
                    // We trigger a flush following the indicated number of operations. However, high load can cause
                    // flushes to run for too long, hence, we trigger the logic every time after we go over the
                    // threshold, just to ensure it gets run when it's needed.
                    ForceCacheFlush(context,
                        counter: ContentLocationDatabaseCounters.NumberOfCacheFlushesTriggeredByUpdates,
                        blocking: false);
                }
            }
            else
            {
                Persist(context, hash, entry);
                Counters[ContentLocationDatabaseCounters.NumberOfPersistedEntries].Increment();
            }
        }

        /// <nodoc />
        protected void Delete(OperationContext context, ShortHash hash)
        {
            Store(context, hash, entry: null);
        }

        /// <summary>
        /// Forces a cache flush.
        /// </summary>
        /// <returns>
        /// The return value is only relevant for tests, and when the in-memory cache is enabled.
        ///
        /// It is true if the current thread either performed or waited for a flush to finish.
        /// </returns>
        internal bool ForceCacheFlush(OperationContext context, ContentLocationDatabaseCounters? counter = null, bool blocking = true)
        {
            if (!IsInMemoryCacheEnabled)
            {
                return false;
            }

            bool renewed = false;
            if (FlushTask.IsCompleted)
            {
                lock (_flushTaskLock)
                {
                    if (FlushTask.IsCompleted)
                    {
                        FlushTask = forceCacheFlushAsync(context, counter);
                        renewed = true;
                    }
                }
            }

            if (blocking)
            {
                FlushTask.GetAwaiter().GetResult();
            }

            return renewed && blocking;

            Task forceCacheFlushAsync(OperationContext context, ContentLocationDatabaseCounters? counter = null)
            {
                if (!IsInMemoryCacheEnabled)
                {
                    return BoolResult.SuccessTask;
                }

                return context.PerformOperationAsync(
                    Tracer,
                    async () =>
                    {
                        // NOTE(jubayard): notice that the count of the dictionary is actually the number of unique
                        // updated entries, which can be much less than the number of actual updates performed (i.e. if
                        // the updates are performed on a single entry). We need to make sure we discount the "precise"
                        // number of updates that are written to disk.
                        long flushedEntries = _cacheUpdatesSinceLastFlush;
                        try
                        {
                            var flushCounters = await _inMemoryCache.FlushAsync(context);
                            return Result.Success(flushCounters);
                        }
                        finally
                        {
                            Interlocked.Add(ref _cacheUpdatesSinceLastFlush, -flushedEntries);
                            ResetFlushTimer();

                            if (counter != null)
                            {
                                Counters[counter.Value].Increment();
                            }
                        }
                    }, extraEndMessage: maybeCounters =>
                    {
                        if (!maybeCounters.Succeeded)
                        {
                            return string.Empty;
                        }

                        var counters = maybeCounters.Value;
                        return $"Persisted={counters[FlushableCache.FlushableCacheCounters.Persisted].Value} Leftover={counters[FlushableCache.FlushableCacheCounters.Leftover].Value} Growth={counters[FlushableCache.FlushableCacheCounters.Growth].Value} FlushingTime={counters[FlushableCache.FlushableCacheCounters.FlushingTime].Duration.TotalMilliseconds}ms CleanupTime={counters[FlushableCache.FlushableCacheCounters.CleanupTime].Duration.TotalMilliseconds}ms";
                    }).ThrowIfFailure();
            }
        }

        private ContentLocationEntry SetMachineExistenceAndUpdateDatabase(OperationContext context, ShortHash hash, MachineId? machine, bool existsOnMachine, long size, UnixTime? lastAccessTime, bool reconciling)
        {
            var created = false;
            var reason = reconciling ? OperationReason.Reconcile : OperationReason.Unknown;
            var priorLocationCount = 0;
            lock (GetLock(hash))
            {
                if (TryGetEntryCore(context, hash, out var entry))
                {
                    var initialEntry = entry;
                    priorLocationCount = entry.Locations.Count;

                    // Don't update machines if entry already contains the machine
                    var machines = machine != null && (entry.Locations[machine.Value] != existsOnMachine)
                        ? new[] { machine.Value }
                        : CollectionUtilities.EmptyArray<MachineId>();

                    // Don't update last access time if the touch frequency interval has not elapsed since last access
                    if (lastAccessTime != null && initialEntry.LastAccessTimeUtc.ToDateTime().IsRecent(lastAccessTime.Value.ToDateTime(), _configuration.TouchFrequency))
                    {
                        lastAccessTime = null;
                    }

                    entry = entry.SetMachineExistence(machines, existsOnMachine, lastAccessTime, size: size >= 0 ? (long?)size : null);

                    if (entry == initialEntry)
                    {
                        // The entry is unchanged.
                        return initialEntry;
                    }

                    if (existsOnMachine)
                    {
                        _nagleOperationTracer.Enqueue((hash, initialEntry.Locations.Count == entry.Locations.Count ? EntryOperation.Touch : EntryOperation.AddMachine, reason));
                    }
                    else
                    {
                        _nagleOperationTracer.Enqueue((hash, machine == null ? EntryOperation.Touch : EntryOperation.RemoveMachine, reason));
                    }
                }
                else
                {
                    if (!existsOnMachine || machine == null)
                    {
                        // Attempting to remove a machine from or touch a missing entry should result in no changes
                        return ContentLocationEntry.Missing;
                    }

                    lastAccessTime = lastAccessTime ?? Clock.UtcNow;
                    var creationTime = UnixTime.Min(lastAccessTime.Value, Clock.UtcNow.ToUnixTime());

                    entry = ContentLocationEntry.Create(MachineIdSet.Empty.SetExistence(new[] { machine.Value }, existsOnMachine), size, lastAccessTime.Value, creationTime);
                    created = true;
                }

                if (machine != null)
                {
                    if (existsOnMachine)
                    {
                        Counters[ContentLocationDatabaseCounters.TotalContentAddedCount].Increment();
                        Counters[ContentLocationDatabaseCounters.TotalContentAddedSize].Add(entry.ContentSize);
                    }
                    else
                    {
                        Counters[ContentLocationDatabaseCounters.TotalContentRemovedCount].Increment();
                        Counters[ContentLocationDatabaseCounters.TotalContentRemovedSize].Add(entry.ContentSize);
                    }
                }

                if (entry.Locations.Count == 0)
                {
                    // Remove the hash when no more locations are registered
                    Delete(context, hash);
                    LogEntryDeletion(hash, reason, entry.ContentSize);
                }
                else
                {
                    Store(context, hash, entry);

                    if (created)
                    {
                        Counters[ContentLocationDatabaseCounters.TotalNumberOfCreatedEntries].Increment();
                        Counters[ContentLocationDatabaseCounters.UniqueContentAddedSize].Add(entry.ContentSize);
                        _nagleOperationTracer.Enqueue((hash, EntryOperation.Create, reason));
                    }
                }

                return entry;
            }
        }

        private void LogEntryDeletion(ShortHash hash, OperationReason reason, long size)
        {
            Counters[ContentLocationDatabaseCounters.TotalNumberOfDeletedEntries].Increment();
            Counters[ContentLocationDatabaseCounters.UniqueContentRemovedSize].Add(size);
            _nagleOperationTracer.Enqueue((hash, EntryOperation.Delete, reason));
        }

        /// <summary>
        /// Performs a compare exchange operation on metadata, while ensuring all invariants are kept. If the
        /// fingerprint is not present, then it is inserted.
        /// </summary>
        /// <param name="context">
        ///     Tracing context.
        /// </param>
        /// <param name="strongFingerprint">
        ///     Full key for ContentHashList value.
        /// </param>
        /// <param name="expected">
        ///     Expected value.
        /// </param>
        /// <param name="replacement">
        ///     Value to put in case the expected value matches.
        /// </param>
        /// <returns>
        ///     Result providing the call's completion status. True if the replacement was completed successfully,
        ///     false otherwise.
        /// </returns>
        public abstract Possible<bool> CompareExchange(OperationContext context, StrongFingerprint strongFingerprint, ContentHashListWithDeterminism expected, ContentHashListWithDeterminism replacement);

        /// <summary>
        /// Load a ContentHashList.
        /// </summary>
        public abstract GetContentHashListResult GetContentHashList(OperationContext context, StrongFingerprint strongFingerprint);

        /// <summary>
        /// Gets known selectors for a given weak fingerprint.
        /// </summary>
        public abstract Result<IReadOnlyList<Selector>> GetSelectors(OperationContext context, Fingerprint weakFingerprint);

        /// <summary>
        /// Enumerates all strong fingerprints currently stored in the cache.
        /// </summary>
        /// <remarks>
        ///     Warning: this function should only ever be used on tests.
        /// </remarks>
        public abstract IEnumerable<StructResult<StrongFingerprint>> EnumerateStrongFingerprints(OperationContext context);

        private object GetLock(ShortHash hash)
        {
            // NOTE: We choose not to use "random" two bytes of the hash because
            // otherwise GC which uses an ordered set of hashes would acquire the same
            // lock over and over again potentially freezing out writers
            return _locks[hash[6] << 8 | hash[3]];
        }

        /// <nodoc />
        public void LocationAdded(OperationContext context, ShortHash hash, MachineId machine, long size, bool reconciling = false, bool updateLastAccessTime = true)
        {
            using (Counters[ContentLocationDatabaseCounters.LocationAdded].Start())
            {
                SetMachineExistenceAndUpdateDatabase(context, hash, machine, existsOnMachine: true, size: size, lastAccessTime: updateLastAccessTime ? Clock.UtcNow : (DateTime?)null, reconciling: reconciling);
            }
        }

        /// <nodoc />
        public void LocationRemoved(OperationContext context, ShortHash hash, MachineId machine, bool reconciling = false)
        {
            using (Counters[ContentLocationDatabaseCounters.LocationRemoved].Start())
            {
                SetMachineExistenceAndUpdateDatabase(context, hash, machine, existsOnMachine: false, size: -1, lastAccessTime: null, reconciling: reconciling);
            }
        }

        /// <nodoc />
        public void ContentTouched(OperationContext context, ShortHash hash, UnixTime accessTime)
        {
            using (Counters[ContentLocationDatabaseCounters.ContentTouched].Start())
            {
                SetMachineExistenceAndUpdateDatabase(context, hash, machine: null, existsOnMachine: false, -1, lastAccessTime: accessTime, reconciling: false);
            }
        }

        /// <summary>
        /// Uses an object pool to fetch a serializer and feed it into the serialization function.
        /// </summary>
        /// <remarks>
        /// We explicitly take and pass the instance as parameters in order to avoid lambda capturing.
        /// </remarks>
        protected byte[] SerializeCore<T>(T instance, Action<T, BuildXLWriter> serializeFunc)
        {
            using var pooledWriter = _writerPool.GetInstance();
            var writer = pooledWriter.Instance.Writer;
            serializeFunc(instance, writer);
            return pooledWriter.Instance.Buffer.ToArray();
        }

        /// <summary>
        /// Uses an object pool to fetch a binary reader and feed it into the deserialization function.
        /// </summary>
        /// <remarks>
        /// Be mindful of avoiding lambda capture when using this function.
        /// </remarks>
        protected T DeserializeCore<T>(byte[] bytes, Func<BuildXLReader, T> deserializeFunc)
        {
            using PooledObjectWrapper<StreamBinaryReader> pooledReader = _readerPool.GetInstance();
            var reader = pooledReader.Instance;
            return reader.Deserialize(new ArraySegment<byte>(bytes), deserializeFunc);
        }

        /// <summary>
        /// Serialize a given <paramref name="entry"/> into a byte stream.
        /// </summary>
        protected byte[] SerializeContentLocationEntry(ContentLocationEntry entry)
        {
            return SerializeCore(entry, (instance, writer) => instance.Serialize(writer));
        }

        /// <summary>
        /// Deserialize <see cref="ContentLocationEntry"/> from an array of bytes.
        /// </summary>
        protected ContentLocationEntry DeserializeContentLocationEntry(byte[] bytes)
        {
            return DeserializeCore(bytes, ContentLocationEntry.Deserialize);
        }

        /// <summary>
        /// Returns true a byte array deserialized into <see cref="ContentLocationEntry"/> would have <paramref name="machineId"/> index set.
        /// </summary>
        /// <remarks>
        /// This is an optimization that allows the clients to "poke" inside the value stored in the database without full deserialization.
        /// The approach is very useful in reconciliation scenarios, when the client wants to obtain content location entries for the current machine only.
        /// </remarks>
        public bool HasMachineId(byte[] bytes, int machineId)
        {
            using var pooledObjectWrapper = _readerPool.GetInstance();
            var pooledReader = pooledObjectWrapper.Instance;
            return pooledReader.Deserialize(
                new ArraySegment<byte>(bytes),
                machineId,
                (localIndex, reader) =>
                {
                    // It is very important for this lambda to be non-capturing, because it will be called
                    // many times.
                    // Avoiding allocations here severely affect performance during reconciliation.
                    _ = reader.ReadInt64Compact();
                    return MachineIdSet.HasMachineId(reader, localIndex);
                });
        }

        /// <inheritdoc />
        public abstract Result<long> GetContentDatabaseSizeBytes();
    }
}
