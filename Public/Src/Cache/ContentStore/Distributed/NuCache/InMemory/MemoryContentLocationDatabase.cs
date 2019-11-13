// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.MemoizationStore.Interfaces.Results;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Utilities;

namespace BuildXL.Cache.ContentStore.Distributed.NuCache.InMemory
{
    /// <summary>
    /// In-memory version of <see cref="ContentLocationDatabase"/>.
    /// </summary>
    public sealed class MemoryContentLocationDatabase : ContentLocationDatabase
    {
        private readonly ConcurrentDictionary<ShortHash, ContentLocationEntry> _map = new ConcurrentDictionary<ShortHash, ContentLocationEntry>();
        private readonly ConcurrentDictionary<string, string> _globalEntryMap = new ConcurrentDictionary<string, string>();

        /// <inheritdoc />
        public MemoryContentLocationDatabase(IClock clock, MemoryContentLocationDatabaseConfiguration configuration, Func<IReadOnlyList<MachineId>> getInactiveMachines)
            : base(clock, configuration, getInactiveMachines)
        {
        }

        /// <inheritdoc />
        protected override BoolResult InitializeCore(OperationContext context)
        {
            // Intentionally doing nothing.
            Tracer.Info(context, "Initializing in-memory content location database.");
            return BoolResult.Success;
        }

        /// <inheritdoc />
        public override void SetGlobalEntry(string key, string value)
        {
            if (value == null)
            {
                _globalEntryMap.TryRemove(key, out _);
            }
            else
            {
                _globalEntryMap[key] = value;
            }
        }

        /// <inheritdoc />
        public override bool TryGetGlobalEntry(string key, out string value)
        {
            return _globalEntryMap.TryGetValue(key, out value);
        }

        /// <inheritdoc />
        protected override bool TryGetEntryCoreFromStorage(OperationContext context, ShortHash hash, out ContentLocationEntry entry)
        {
            entry = GetContentLocationEntry(hash);
            return entry != null && !entry.IsMissing;
        }

        /// <inheritdoc />
        internal override void Persist(OperationContext context, ShortHash hash, ContentLocationEntry entry)
        {
            // consider merging the values. Right now we always reconstruct the entry.
            _map.AddOrUpdate(hash, key => entry, (key, old) => entry);
        }


        /// <inheritdoc />
        public override Possible<bool> CompareExchange(OperationContext context, StrongFingerprint strongFingerprint, ContentHashListWithDeterminism expected, ContentHashListWithDeterminism replacement)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public override GetContentHashListResult GetContentHashList(OperationContext context, StrongFingerprint strongFingerprint)
        {
            throw new NotImplementedException();
        }
        
        /// <inheritdoc />
        public override Result<IReadOnlyList<Selector>> GetSelectors(OperationContext context, Fingerprint weakFingerprint)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public override IEnumerable<StructResult<StrongFingerprint>> EnumerateStrongFingerprints(OperationContext context)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        protected override IEnumerable<ShortHash> EnumerateSortedKeysFromStorage(CancellationToken token)
        {
            return _map.Keys.OrderBy(h => h);
        }

        /// <inheritdoc />
        protected override IEnumerable<(ShortHash key, ContentLocationEntry entry)> EnumerateEntriesWithSortedKeysFromStorage(
            CancellationToken token,
            EnumerationFilter filter = null)
        {
            return _map
                .OrderBy(kvp => kvp.Key)
                .SkipWhile(kvp => filter?.StartingPoint != null && filter.StartingPoint > kvp.Key)
                .Where(kvp => filter?.ShouldEnumerate == null || filter.ShouldEnumerate?.Invoke(SerializeContentLocationEntry(kvp.Value)) == true)
                .Select(kvp => (kvp.Key, kvp.Value));
        }

        /// <inheritdoc />
        protected override BoolResult SaveCheckpointCore(OperationContext context, Interfaces.FileSystem.AbsolutePath checkpointDirectory)
        {
            return BoolResult.Success;
        }

        /// <inheritdoc />
        protected override BoolResult RestoreCheckpointCore(OperationContext context, Interfaces.FileSystem.AbsolutePath checkpointDirectory)
        {
            return BoolResult.Success;
        }

        /// <inheritdoc />
        public override bool IsImmutable(Interfaces.FileSystem.AbsolutePath dbFile)
        {
            return false;
        }

        private ContentLocationEntry GetContentLocationEntry(ShortHash hash)
        {
            if (_map.TryGetValue(hash, out var entry))
            {
                return ContentLocationEntry.Create(entry.Locations, entry.ContentSize, lastAccessTimeUtc: Clock.UtcNow, creationTimeUtc: null);
            }

            return ContentLocationEntry.Missing;
        }

        /// <inheritdoc />
        protected override void UpdateClusterStateCore(OperationContext context, ClusterState clusterState, bool write)
        {
        }

        /// <inheritdoc />
        protected override BoolResult GarbageCollectMetadataCore(OperationContext context)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public override Result<long> GetContentDatabaseSizeBytes()
        {
            throw new NotImplementedException();
        }
    }
}
