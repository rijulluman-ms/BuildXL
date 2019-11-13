// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

extern alias Async;

using System;
using System.Diagnostics.ContractsLight;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.MemoizationStore.Interfaces.Caches;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Cache.MemoizationStore.Interfaces.Stores;
using BuildXL.Cache.MemoizationStore.Tracing;

namespace BuildXL.Cache.MemoizationStore.Sessions
{
    /// <summary>
    ///     A reference implementation of <see cref="ICache"/> that represents a single level of content and metadata.
    /// </summary>
    public class OneLevelCache : ICache, IContentStore, IStreamStore, IRepairStore, ICopyRequestHandler, IPushFileHandler
    {
        private readonly CacheTracer _tracer = new CacheTracer(nameof(OneLevelCache));

        /// <summary>
        ///     Exposes the ContentStore to subclasses.
        /// </summary>
        protected readonly IContentStore ContentStore;

        /// <summary>
        ///     Exposes the MemoizationStore to subclasses.
        /// </summary>
        protected readonly IMemoizationStore MemoizationStore;

        private bool _disposed;

        /// <summary>
        ///     Determines if the content session will be passed to the memoization store when constructing a non-readonly session.
        /// </summary>
        private readonly bool _passContentToMemoization = true;

        /// <summary>
        ///     Initializes a new instance of the <see cref="OneLevelCache" /> class.
        /// </summary>
        /// <remarks>
        ///     It is assumed that the produced sessions are different objects.
        /// </remarks>
        public OneLevelCache(Func<IContentStore> contentStoreFunc, Func<IMemoizationStore> memoizationStoreFunc, Guid id)
        {
            Contract.Requires(contentStoreFunc != null);
            Contract.Requires(memoizationStoreFunc != null);

            ContentStore = contentStoreFunc();
            MemoizationStore = memoizationStoreFunc();
            Id = id;

            Contract.Assert(!ReferenceEquals(ContentStore, MemoizationStore));
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="OneLevelCache" /> class, with an option to configure whether the content session will be passed to the memoization store
        ///     when creating a non-readonly session.
        /// </summary>
        public OneLevelCache(Func<IContentStore> contentStoreFunc, Func<IMemoizationStore> memoizationStoreFunc, Guid id, bool passContentToMemoization)
            : this(contentStoreFunc, memoizationStoreFunc, id)
        {
            _passContentToMemoization = passContentToMemoization;
        }

        /// <inheritdoc />
        public bool StartupStarted { get; private set; }

        /// <inheritdoc />
        public bool StartupCompleted { get; private set; }

        /// <inheritdoc />
        public bool ShutdownStarted { get; private set; }

        /// <inheritdoc />
        public bool ShutdownCompleted { get; private set; }

        /// <inheritdoc />
        public Guid Id { get; }

        /// <summary>
        ///     Hook for subclass to process before startup.
        /// </summary>
        protected virtual Task<BoolResult> PreStartupAsync(Context context)
        {
            return Task.FromResult(BoolResult.Success);
        }

        /// <inheritdoc />
        public Task<BoolResult> StartupAsync(Context context)
        {
            StartupStarted = true;

            return StartupCall<CacheTracer>.RunAsync(_tracer, context, async () =>
            {
                var preStartupResult = await PreStartupAsync(context).ConfigureAwait(false);
                if (!preStartupResult.Succeeded)
                {
                    return preStartupResult;
                }

                var contentStoreTask = Task.Run(() => ContentStore.StartupAsync(context));
                var memoizationStoreResult = await MemoizationStore.StartupAsync(context).ConfigureAwait(false);
                var contentStoreResult = await contentStoreTask.ConfigureAwait(false);

                BoolResult result;

                if (!contentStoreResult.Succeeded || !memoizationStoreResult.Succeeded)
                {
                    var sb = new StringBuilder();

                    if (contentStoreResult.Succeeded)
                    {
                        var r = await ContentStore.ShutdownAsync(context).ConfigureAwait(false);
                        if (!r.Succeeded)
                        {
                            sb.AppendFormat($"Content store shutdown failed, error=[{r}]");
                        }
                    }
                    else
                    {
                        sb.AppendFormat($"Content store startup failed, error=[{contentStoreResult}]");
                    }

                    if (memoizationStoreResult.Succeeded)
                    {
                        var r = await MemoizationStore.ShutdownAsync(context).ConfigureAwait(false);
                        if (!r.Succeeded)
                        {
                            sb.Append(sb.Length > 0 ? ", " : string.Empty);
                            sb.AppendFormat($"Memoization store shutdown failed, error=[{memoizationStoreResult}]");
                        }
                    }
                    else
                    {
                        sb.Append(sb.Length > 0 ? ", " : string.Empty);
                        sb.AppendFormat($"Memoization store startup failed, error=[{memoizationStoreResult}]");
                    }

                    result = new BoolResult(sb.ToString());
                }
                else
                {
                    result = BoolResult.Success;
                }

                StartupCompleted = true;
                return result;
            });
        }

        /// <inheritdoc />
        public Task<BoolResult> ShutdownAsync(Context context)
        {
            ShutdownStarted = true;

            return ShutdownCall<CacheTracer>.RunAsync(_tracer, context, async () =>
            {
                var statsResult = await StatsAsync(context).ConfigureAwait(false);

                var contentStoreTask = Task.Run(() => ContentStore.ShutdownAsync(context));
                var memoizationStoreResult = await MemoizationStore.ShutdownAsync(context).ConfigureAwait(false);
                var contentStoreResult = await contentStoreTask.ConfigureAwait(false);

                BoolResult result;
                if (contentStoreResult.Succeeded && memoizationStoreResult.Succeeded)
                {
                    result = BoolResult.Success;
                }
                else
                {
                    var sb = new StringBuilder();
                    if (!contentStoreResult.Succeeded)
                    {
                        sb.AppendFormat($"Content store shutdown failed, error=[{contentStoreResult}]");
                    }

                    if (!memoizationStoreResult.Succeeded)
                    {
                        sb.Append(sb.Length > 0 ? ", " : string.Empty);
                        sb.AppendFormat($"Memoization store shutdown failed, error=[{memoizationStoreResult}]");
                    }

                    result = new BoolResult(sb.ToString());
                }

                if (statsResult.Succeeded)
                {
#if NET_FRAMEWORK
                    LocalCacheStatsEventSource.Instance.Stats(statsResult.CounterSet);
#endif
                    statsResult.CounterSet.LogOrderedNameValuePairs(s => _tracer.Debug(context, s));
                }

                ShutdownCompleted = true;
                return result;
            });
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Dispose(true);
            GC.SuppressFinalize(this);

            _disposed = true;
        }

        /// <summary>
        ///     Protected implementation of Dispose pattern.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            MemoizationStore.Dispose();
            ContentStore.Dispose();
        }

        /// <inheritdoc />
        public CreateSessionResult<IReadOnlyCacheSession> CreateReadOnlySession(Context context, string name, ImplicitPin implicitPin)
        {
            return Tracing.CreateReadOnlySessionCall.Run(_tracer, context, name, () =>
            {
                var createContentResult = ContentStore.CreateReadOnlySession(context, name, implicitPin);
                if (!createContentResult.Succeeded)
                {
                    return new CreateSessionResult<IReadOnlyCacheSession>(createContentResult, "Content session creation failed");
                }
                var contentReadOnlySession = createContentResult.Session;

                var createMemoizationResult = MemoizationStore.CreateReadOnlySession(context, name);
                if (!createMemoizationResult.Succeeded)
                {
                    return new CreateSessionResult<IReadOnlyCacheSession>(createMemoizationResult, "Memoization session creation failed");
                }
                var memoizationReadOnlySession = createMemoizationResult.Session;

                var session = new ReadOnlyOneLevelCacheSession(name, implicitPin, memoizationReadOnlySession, contentReadOnlySession);
                return new CreateSessionResult<IReadOnlyCacheSession>(session);
            });
        }

        /// <inheritdoc />
        public CreateSessionResult<ICacheSession> CreateSession(Context context, string name, ImplicitPin implicitPin)
        {
            return Tracing.CreateSessionCall.Run(_tracer, context, name, () =>
            {
                var createContentResult = ContentStore.CreateSession(context, name, implicitPin);
                if (!createContentResult.Succeeded)
                {
                    return new CreateSessionResult<ICacheSession>(createContentResult, "Content session creation failed");
                }
                var contentSession = createContentResult.Session;

                var createMemoizationResult = _passContentToMemoization
                    ? MemoizationStore.CreateSession(context, name, contentSession)
                    : MemoizationStore.CreateSession(context, name);

                if (!createMemoizationResult.Succeeded)
                {
                    return new CreateSessionResult<ICacheSession>(createMemoizationResult, "Memoization session creation failed");
                }
                var memoizationSession = createMemoizationResult.Session;

                var session = new OneLevelCacheSession(name, implicitPin, memoizationSession, contentSession);
                return new CreateSessionResult<ICacheSession>(session);
            });
        }

        /// <inheritdoc />
        public Task<GetStatsResult> GetStatsAsync(Context context)
        {
            return GetStatsCall<CacheTracer>.RunAsync(_tracer, new OperationContext(context), () => StatsAsync(context));
        }

        private async Task<GetStatsResult> StatsAsync(Context context)
        {
            var counters = new CounterSet();

            var statsResult = await ContentStore.GetStatsAsync(context).ConfigureAwait(false);

            if (!statsResult.Succeeded)
            {
                return statsResult;
            }

            counters.Merge(statsResult.CounterSet);

            statsResult = await MemoizationStore.GetStatsAsync(context).ConfigureAwait(false);

            if (!statsResult.Succeeded)
            {
                return statsResult;
            }

            counters.Merge(statsResult.CounterSet);

            return new GetStatsResult(counters);
        }

        /// <inheritdoc />
        public Async::System.Collections.Generic.IAsyncEnumerable<StructResult<StrongFingerprint>> EnumerateStrongFingerprints(Context context)
        {
            return MemoizationStore.EnumerateStrongFingerprints(context);
        }

        /// <inheritdoc />
        public async Task<OpenStreamResult> StreamContentAsync(Context context, ContentHash contentHash)
        {
            if (ContentStore is IStreamStore innerStreamStore)
            {
                return await innerStreamStore.StreamContentAsync(context, contentHash);
            }

            return new OpenStreamResult($"{ContentStore} does not implement {nameof(IStreamStore)} in {nameof(OneLevelCache)}.");
        }

        /// <inheritdoc />
        public async Task<FileExistenceResult> CheckFileExistsAsync(Context context, ContentHash contentHash)
        {
            if (ContentStore is IStreamStore innerStreamStore)
            {
                return await innerStreamStore.CheckFileExistsAsync(context, contentHash);
            }

            return new FileExistenceResult(FileExistenceResult.ResultCode.Error, $"{ContentStore} does not implement {nameof(IStreamStore)} in {nameof(OneLevelCache)}.");
        }

        /// <inheritdoc />
        public async Task<StructResult<long>> RemoveFromTrackerAsync(Context context)
        {
            if (ContentStore is IRepairStore innerRepairStore)
            {
                return await innerRepairStore.RemoveFromTrackerAsync(context);
            }

            return new StructResult<long>($"{ContentStore} does not implement {nameof(IRepairStore)} in {nameof(OneLevelCache)}.");
        }

        /// <inheritdoc />
        public async Task<BoolResult> HandleCopyFileRequestAsync(Context context, ContentHash hash)
        {
            if (ContentStore is ICopyRequestHandler innerCopyStore)
            {
                return await innerCopyStore.HandleCopyFileRequestAsync(context, hash);
            }

            return new BoolResult($"{ContentStore} does not implement {nameof(ICopyRequestHandler)} in {nameof(OneLevelCache)}.");
        }

        CreateSessionResult<IReadOnlyContentSession> IContentStore.CreateReadOnlySession(Context context, string name, ImplicitPin implicitPin)
        {
            return CreateReadOnlySession(context, name, implicitPin).Map(session => (IReadOnlyContentSession)session);
        }

        CreateSessionResult<IContentSession> IContentStore.CreateSession(Context context, string name, ImplicitPin implicitPin)
        {
            return CreateSession(context, name, implicitPin).Map(session => (IContentSession)session);
        }

        /// <inheritdoc />
        public Task<DeleteResult> DeleteAsync(Context context, ContentHash contentHash)
        {
            return ContentStore.DeleteAsync(context, contentHash);
        }

        /// <inheritdoc />
        public void PostInitializationCompleted(Context context, BoolResult result)
        {
            ContentStore.PostInitializationCompleted(context, result);
        }

        /// <inheritdoc />
        public Task<PutResult> HandlePushFileAsync(Context context, ContentHash hash, AbsolutePath sourcePath, CancellationToken token)
        {
            if (ContentStore is IPushFileHandler handler)
            {
                return handler.HandlePushFileAsync(context, hash, sourcePath, token);
            }

            return Task.FromResult(new PutResult(new InvalidOperationException($"{nameof(ContentStore)} does not implement {nameof(IPushFileHandler)}"), hash));
        }

        /// <inheritdoc />
        public bool HasContentLocally(Context context, ContentHash hash)
        {
            if (ContentStore is IPushFileHandler handler)
            {
                return handler.HasContentLocally(context, hash);
            }

            return false;
        }
    }
}
