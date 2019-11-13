// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.Sessions;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Utilities.Tasks;
using BuildXL.Utilities.Tracing;
using static BuildXL.Cache.ContentStore.Distributed.Stores.DistributedContentStoreSettings;

namespace BuildXL.Cache.ContentStore.Distributed.Stores
{
    /// <summary>
    /// Handles copies from remote locations to a local store
    /// </summary>
    /// <typeparam name="T">The content locations being stored.</typeparam>
    public class DistributedContentCopier<T> : StartupShutdownSlimBase, IDistributedContentCopier
        where T : PathBase
    {
        // Gate to control the maximum number of simultaneously active IO operations.
        private readonly SemaphoreSlim _ioGate;

        // Gate to control the maximum number of simultaneously active proactive copies.
        private readonly SemaphoreSlim _proactiveCopyIoGate;

        private readonly IReadOnlyList<TimeSpan> _retryIntervals;
        private readonly TimeSpan _timeoutForProactiveCopies;
        private readonly int _maxRetryCount;
        private readonly DisposableDirectory _tempFolderForCopies;
        private readonly IFileCopier<T> _remoteFileCopier;
        private readonly IProactiveCopier _copyRequester;
        private readonly IFileExistenceChecker<T> _remoteFileExistenceChecker;
        private readonly IPathTransformer<T> _pathTransformer;
        private readonly IContentLocationStore _contentLocationStore;

        private readonly DistributedContentStoreSettings _settings;
        private readonly IAbsFileSystem _fileSystem;

        private readonly CounterCollection<DistributedContentCopierCounters> _counters = new CounterCollection<DistributedContentCopierCounters>();

        private readonly AbsolutePath _workingDirectory;

        /// <inheritdoc />
        protected override Tracer Tracer { get; } = new Tracer(nameof(DistributedContentCopier<T>));

        /// <nodoc />
        public int CurrentIoGateCount => _ioGate.CurrentCount;

        /// <nodoc />
        public DistributedContentCopier(
            AbsolutePath workingDirectory,
            DistributedContentStoreSettings settings,
            IAbsFileSystem fileSystem,
            IFileCopier<T> fileCopier,
            IFileExistenceChecker<T> fileExistenceChecker,
            IProactiveCopier copyRequester,
            IPathTransformer<T> pathTransformer,
            IContentLocationStore contentLocationStore)
        {
            Contract.Requires(settings != null);
            Contract.Requires(settings.ParallelHashingFileSizeBoundary >= -1);

            _settings = settings;
            _tempFolderForCopies = new DisposableDirectory(fileSystem, workingDirectory / "Temp");
            _remoteFileCopier = fileCopier;
            _remoteFileExistenceChecker = fileExistenceChecker;
            _copyRequester = copyRequester;
            _contentLocationStore = contentLocationStore;
            _pathTransformer = pathTransformer;
            _fileSystem = fileSystem;

            _workingDirectory = _tempFolderForCopies.Path;

            _ioGate = new SemaphoreSlim(_settings.MaxConcurrentCopyOperations);
            _proactiveCopyIoGate = new SemaphoreSlim(_settings.MaxConcurrentProactiveCopyOperations);
            _retryIntervals = settings.RetryIntervalForCopies;
            _maxRetryCount = settings.MaxRetryCount;
            _timeoutForProactiveCopies = settings.TimeoutForProactiveCopies;
        }

        /// <inheritdoc />
        protected override Task<BoolResult> StartupCoreAsync(OperationContext context)
        {
            if (_settings.CleanRandomFilesAtRoot)
            {
                foreach (var file in _fileSystem.EnumerateFiles(_workingDirectory.Parent, EnumerateOptions.None))
                {
                    if (IsRandomFile(file.FullPath))
                    {
                        Tracer.Debug(context, $"Deleting random file {file.FullPath} at root.");
                        _fileSystem.DeleteFile(file.FullPath);
                    }
                }
            }

            return base.StartupCoreAsync(context);
        }

        private bool IsRandomFile(AbsolutePath file)
        {
            var fileName = file.GetFileName();
            return fileName.StartsWith("random-", StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        protected override Task<BoolResult> ShutdownCoreAsync(OperationContext context)
        {
            _tempFolderForCopies.Dispose();

            return base.ShutdownCoreAsync(context);
        }

        /// <inheritdoc />
        public async Task<PutResult> TryCopyAndPutAsync(
            OperationContext operationContext,
            ContentHashWithSizeAndLocations hashInfo,
            Func<(CopyFileResult copyResult, AbsolutePath tempLocation, int attemptCount), Task<PutResult>> handleCopyAsync,
            Action<IReadOnlyList<MachineLocation>> handleBadLocations = null)
        {
            var cts = operationContext.Token;

            try
            {
                PutResult putResult = null;
                var badContentLocations = new HashSet<MachineLocation>();
                var missingContentLocations = new HashSet<MachineLocation>();
                var lastFailureTimes = new List<DateTime>();
                int attemptCount = 0;
                TimeSpan waitDelay = TimeSpan.Zero;

                // _retryIntervals controls how many cycles we go through of copying from a list of locations
                // It also has the increasing wait times between cycles
                while (attemptCount < _retryIntervals.Count && (putResult == null || !putResult))
                {
                    bool retry;

                    (putResult, retry) = await WalkLocationsAndCopyAndPutAsync(
                        operationContext,
                        _workingDirectory,
                        hashInfo,
                        badContentLocations,
                        missingContentLocations,
                        lastFailureTimes,
                        attemptCount,
                        waitDelay,
                        handleCopyAsync);

                    if (putResult || operationContext.Token.IsCancellationRequested)
                    {
                        break;
                    }

                    if (missingContentLocations.Count == hashInfo.Locations.Count)
                    {
                        Tracer.Warning(operationContext, $"{AttemptTracePrefix(attemptCount)} All replicas {hashInfo.Locations.Count} are reported missing. Not retrying for hash {hashInfo.ContentHash.ToShortString()}.");
                        break;
                    }

                    if (!retry)
                    {
                        Tracer.Warning(operationContext, $"{AttemptTracePrefix(attemptCount)} Cannot place {hashInfo.ContentHash.ToShortString()} due to error: {putResult.ErrorMessage}. Not retrying for hash {hashInfo.ContentHash.ToShortString()}.");
                        break;
                    }

                    attemptCount++;

                    if (attemptCount < _retryIntervals.Count)
                    {
                        long waitTicks = _retryIntervals[attemptCount].Ticks;

                        // Every location uses the same waitDelay per cycle
                        // Randomize the wait delay to `[0.5 * delay, 1.5 * delay)`
                        waitDelay = TimeSpan.FromTicks((long)((waitTicks / 2) + (waitTicks * ThreadSafeRandom.Generator.NextDouble())));

                        // Log with the original attempt count
                        Tracer.Warning(operationContext, $"{AttemptTracePrefix(attemptCount - 1)} All replicas {hashInfo.Locations.Count} failed. Retrying for hash {hashInfo.ContentHash.ToShortString()} in {waitDelay.TotalMilliseconds}ms...");
                    }
                    else
                    {
                        break;
                    }
                }

                // now that retries are exhausted, combine the missing and bad locations.
                badContentLocations.UnionWith(missingContentLocations);

                if (badContentLocations.Any())
                {
                    // This will go away when LLS is the only content location store
                    handleBadLocations?.Invoke(badContentLocations.ToList());
                }

                if (!putResult.Succeeded)
                {
                    traceCopyFailed(operationContext);
                }
                else
                {
                    Tracer.TrackMetric(operationContext, "RemoteBytesCount", putResult.ContentSize);
                    _counters[DistributedContentCopierCounters.RemoteBytes].Add(putResult.ContentSize);
                    _counters[DistributedContentCopierCounters.RemoteFilesCopied].Increment();
                }

                return putResult;
            }
            catch (Exception ex)
            {
                traceCopyFailed(operationContext);

                if (cts.IsCancellationRequested)
                {
                    return CreateCanceledPutResult();
                }

                return new ErrorResult(ex).AsResult<PutResult>();
            }

            void traceCopyFailed(Context c)
            {
                Tracer.TrackMetric(c, "RemoteCopyFileFailed", 1);
                _counters[DistributedContentCopierCounters.RemoteFilesFailedCopy].Increment();
            }
        }

        /// <summary>
        /// Requests another machine to copy from the current machine.
        /// </summary>
        public Task<BoolResult> RequestCopyFileAsync(OperationContext context, ContentHash hash, MachineLocation targetLocation, bool isInsideRing)
        {
            return _proactiveCopyIoGate.GatedOperationAsync(async ts =>
                {
                    using var cts = new CancellationTokenSource();
                    cts.CancelAfter(_timeoutForProactiveCopies);
                    // Creating new operation context with a new token, but the newly created context 
                    // still would have the same tracing context to simplify proactive copy trace analysis.
                    var innerContext = context.WithCancellationToken(cts.Token);
                    return await context.PerformOperationAsync(
                        Tracer,
                        operation: () => _copyRequester.RequestCopyFileAsync(innerContext, hash, targetLocation),
                        traceOperationStarted: false,
                        extraEndMessage: result =>
                            $"ContentHash={hash.ToShortString()} " +
                            $"TargetLocation=[{targetLocation}] " +
                            $"InsideRing={isInsideRing} " +
                            $"IOGate.OccupiedCount={_settings.MaxConcurrentProactiveCopyOperations - _proactiveCopyIoGate.CurrentCount} " +
                            $"IOGate.Wait={ts.TotalMilliseconds}ms. " +
                            $"Timeout={_timeoutForProactiveCopies} " +
                            $"TimedOut={cts.Token.IsCancellationRequested}"
                        );
                },
                context.Token);
        }

        /// <summary>
        /// Pushes content to another machine.
        /// </summary>
        public Task<BoolResult> PushFileAsync(OperationContext context, ContentHash hash, MachineLocation targetLocation, Func<Task<Stream>> streamFactory, bool isInsideRing)
        {
            return _proactiveCopyIoGate.GatedOperationAsync(ts =>
            {
                var cts = new CancellationTokenSource();
                cts.CancelAfter(_timeoutForProactiveCopies);
                // Creating new operation context with a new token, but the newly created context 
                // still would have the same tracing context to simplify proactive copy trace analysis.
                var innerContext = context.WithCancellationToken(cts.Token);
                return context.PerformOperationAsync(
                    Tracer,
                    operation: () => _copyRequester.PushFileAsync(innerContext, hash, streamFactory, targetLocation),
                    traceOperationStarted: false,
                    extraEndMessage: result =>
                        $"ContentHash={hash.ToShortString()} " +
                        $"TargetLocation=[{targetLocation}] " +
                        $"InsideRing={isInsideRing} " +
                        $"IOGate.OccupiedCount={_settings.MaxConcurrentProactiveCopyOperations - _proactiveCopyIoGate.CurrentCount} " +
                        $"IOGate.Wait={ts.TotalMilliseconds}ms. " +
                        $"Timeout={_timeoutForProactiveCopies} " +
                        $"TimedOut={cts.Token.IsCancellationRequested}"
                    );
            },
                context.Token);
        }

        private PutResult CreateCanceledPutResult() => new ErrorResult("The operation was canceled").AsResult<PutResult>();
        private PutResult CreateMaxRetryPutResult() => new ErrorResult($"Maximum total retries of {_maxRetryCount} attempted").AsResult<PutResult>();

        /// <nodoc />
        private async Task<(PutResult result, bool retry)> WalkLocationsAndCopyAndPutAsync(
            OperationContext context,
            AbsolutePath workingFolder,
            ContentHashWithSizeAndLocations hashInfo,
            HashSet<MachineLocation> badContentLocations,
            HashSet<MachineLocation> missingContentLocations,
            List<DateTime> lastFailureTimes,
            int attemptCount,
            TimeSpan waitDelay,
            Func<(CopyFileResult copyResult, AbsolutePath tempLocation, int attemptCount), Task<PutResult>> handleCopyAsync)
        {
            var cts = context.Token;

            // before each retry, clear the list of bad locations so we can retry them all.
            // this helps isolate transient network errors.
            badContentLocations.Clear();
            string lastErrorMessage = null;

            for (int replicaIndex = 0; replicaIndex < hashInfo.Locations.Count; replicaIndex++)
            {
                var location = hashInfo.Locations[replicaIndex];

                // Currently everytime we increment attemptCount's value, we go through every location in hashInfo and try to copy.
                // We add one because replicaIndex is indexed from zero.
                // If we reach over maximum retries, return an put result stating so, and no longer retry
                var totalRetryCount = attemptCount * hashInfo.Locations.Count + replicaIndex + 1;
                if (totalRetryCount > _maxRetryCount)
                {
                    Tracer.Debug(
                            context,
                            $"{AttemptTracePrefix(attemptCount)} Reached maximum number of total retries of {_maxRetryCount}.");
                    return (result: CreateMaxRetryPutResult(), retry: false);
                }

                // if the file is explicitly reported missing by the remote, don't bother retrying.
                if (missingContentLocations.Contains(location))
                {
                    continue;
                }

                // If there is a wait time, determine how much longer we need to wait
                if (!waitDelay.Equals(TimeSpan.Zero))
                {
                    TimeSpan waitedTime = DateTime.Now - lastFailureTimes[replicaIndex];
                    if (waitedTime < waitDelay)
                    {
                        await Task.Delay(waitDelay - waitedTime, cts);
                    }
                }

                var sourcePath = _pathTransformer.GeneratePath(hashInfo.ContentHash, location.Data);

                var tempLocation = AbsolutePath.CreateRandomFileName(workingFolder);

                (PutResult result, bool retry) reportCancellationRequested()
                {
                    Tracer.Debug(
                        context,
                        $"{AttemptTracePrefix(attemptCount)}: Could not copy file with hash {hashInfo.ContentHash.ToShortString()} to temp path {tempLocation} because cancellation was requested.");
                    return (result: CreateCanceledPutResult(), retry: false);
                }

                // Both Puts will attempt to Move the file into the cache. If the Put is successful, then the temporary file
                // does not need to be deleted. If anything else goes wrong, then the temporary file must be removed.
                bool deleteTempFile = true;

                try
                {
                    if (cts.IsCancellationRequested)
                    {
                        return reportCancellationRequested();
                    }

                    // Gate entrance to both the copy logic and the logging which surrounds it
                    CopyFileResult copyFileResult = null;
                    try
                    {
                        copyFileResult = await _ioGate.GatedOperationAsync(ts => context.PerformOperationAsync(
                            Tracer,
                            async () =>
                            {
                                return await TaskUtilities.AwaitWithProgressReporting(
                                    task: CopyFileAsync(context, sourcePath, tempLocation, hashInfo, cts),
                                    period: TimeSpan.FromMinutes(5),
                                    action: timeSpan => Tracer.Debug(context, $"{Tracer.Name}.RemoteCopyFile from[{location}]) via stream in progress {(int)timeSpan.TotalSeconds}s."),
                                    reportImmediately: false,
                                    reportAtEnd: false);
                            },
                            traceOperationStarted: false,
                            traceOperationFinished: true,
                            // _ioGate.CurrentCount returns the number of free slots, but we need to print the number of occupied slots instead.
                            extraEndMessage: (result) =>
                                $"contentHash=[{hashInfo.ContentHash.ToShortString()}] " +
                                $"from=[{sourcePath}] " +
                                $"size=[{result.Size ?? hashInfo.Size}] " +
                                $"trusted={_settings.UseTrustedHash(result.Size ?? hashInfo.Size)} " +
                                (result.Succeeded ? $"attempt={attemptCount} replica={replicaIndex} " : string.Empty) +
                                (result.TimeSpentHashing.HasValue ? $"timeSpentHashing={result.TimeSpentHashing.Value.TotalMilliseconds}ms " : string.Empty) +
                                $"IOGate.OccupiedCount={_settings.MaxConcurrentCopyOperations - _ioGate.CurrentCount} " +
                                $"IOGate.Wait={ts.TotalMilliseconds}ms.",
                            caller: "RemoteCopyFile",
                            counter: _counters[DistributedContentCopierCounters.RemoteCopyFile]), cts);

                        if (copyFileResult.TimeSpentHashing.HasValue)
                        {
                            Tracer.TrackMetric(context, "CopyHashingTimeMs", (long)copyFileResult.TimeSpentHashing.Value.TotalMilliseconds);
                        }
                    }
                    catch (Exception e) when (e is OperationCanceledException)
                    {
                        // Handles both OperationCanceledException and TaskCanceledException (TaskCanceledException derives from OperationCanceledException)
                        return reportCancellationRequested();
                    }

                    if (cts.IsCancellationRequested)
                    {
                        return reportCancellationRequested();
                    }

                    if (copyFileResult != null)
                    {
                        switch (copyFileResult.Code)
                        {
                            case CopyFileResult.ResultCode.Success:
                                _contentLocationStore.ReportReputation(location, MachineReputation.Good);
                                break;
                            case CopyFileResult.ResultCode.FileNotFoundError:
                                lastErrorMessage = $"Could not copy file with hash {hashInfo.ContentHash.ToShortString()} from path {sourcePath} to path {tempLocation} due to an error with the sourcepath: {copyFileResult}";
                                Tracer.Warning(
                                    context,
                                    $"{AttemptTracePrefix(attemptCount)} {lastErrorMessage} Trying another replica.");
                                missingContentLocations.Add(location);
                                _contentLocationStore.ReportReputation(location, MachineReputation.Missing);
                                break;
                            case CopyFileResult.ResultCode.SourcePathError:
                                lastErrorMessage = $"Could not copy file with hash {hashInfo.ContentHash.ToShortString()} from path {sourcePath} to path {tempLocation} due to an error with the sourcepath: {copyFileResult}";
                                Tracer.Warning(
                                    context,
                                    $"{AttemptTracePrefix(attemptCount)} {lastErrorMessage} Trying another replica.");
                                _contentLocationStore.ReportReputation(location, MachineReputation.Bad);
                                badContentLocations.Add(location);
                                break;
                            case CopyFileResult.ResultCode.DestinationPathError:
                                lastErrorMessage = $"Could not copy file with hash {hashInfo.ContentHash.ToShortString()} from path {sourcePath} to temp path {tempLocation} due to an error with the destination path: {copyFileResult}";
                                Tracer.Warning(
                                    context,
                                    $"{AttemptTracePrefix(attemptCount)} {lastErrorMessage} Not trying another replica.");
                                return (result: new ErrorResult(copyFileResult).AsResult<PutResult>(), retry: true);
                            case CopyFileResult.ResultCode.CopyTimeoutError:
                                lastErrorMessage = $"Could not copy file with hash {hashInfo.ContentHash.ToShortString()} from path {sourcePath} to path {tempLocation} due to copy timeout: {copyFileResult}";
                                Tracer.Warning(
                                    context,
                                    $"{AttemptTracePrefix(attemptCount)} {lastErrorMessage} Trying another replica.");
                                _contentLocationStore.ReportReputation(location, MachineReputation.Timeout);
                                break;
                            case CopyFileResult.ResultCode.CopyBandwidthTimeoutError:
                                lastErrorMessage = $"Could not copy file with hash {hashInfo.ContentHash.ToShortString()} from path {sourcePath} to path {tempLocation} due to insufficient bandwidth timeout: {copyFileResult}";
                                Tracer.Warning(
                                    context,
                                    $"{AttemptTracePrefix(attemptCount)} {lastErrorMessage} Trying another replica.");
                                _contentLocationStore.ReportReputation(location, MachineReputation.Timeout);
                                break;
                            case CopyFileResult.ResultCode.InvalidHash:
                                lastErrorMessage = $"Could not copy file with hash {hashInfo.ContentHash.ToShortString()} from path {sourcePath} to path {tempLocation} due to invalid hash: {copyFileResult}";
                                Tracer.Warning(
                                    context,
                                    $"{AttemptTracePrefix(attemptCount)} {lastErrorMessage} {copyFileResult}");
                                break;
                            case CopyFileResult.ResultCode.Unknown:
                                lastErrorMessage = $"Could not copy file with hash {hashInfo.ContentHash.ToShortString()} from path {sourcePath} to temp path {tempLocation} due to an internal error: {copyFileResult}";
                                Tracer.Warning(
                                    context,
                                    $"{AttemptTracePrefix(attemptCount)} {lastErrorMessage} Not trying another replica.");
                                _contentLocationStore.ReportReputation(location, MachineReputation.Bad);
                                break;
                            default:
                                lastErrorMessage = $"File copier result code {copyFileResult.Code} is not recognized";
                                return (result: new ErrorResult(copyFileResult, $"{AttemptTracePrefix(attemptCount)} {lastErrorMessage}").AsResult<PutResult>(), retry: true);
                        }

                        if (copyFileResult.Succeeded)
                        {
                            // The copy succeeded, but it is possible that the resulting size doesn't match an expected one.
                            if (hashInfo.Size != -1 && copyFileResult.Size != null && hashInfo.Size != copyFileResult.Size.Value)
                            {
                                lastErrorMessage =
                                    $"Contenthash {hashInfo.ContentHash.ToShortString()} at location {location} has content size {copyFileResult.Size.Value} mismatch from {hashInfo.Size}";
                                Tracer.Warning(
                                    context,
                                    $"{AttemptTracePrefix(attemptCount)} {lastErrorMessage} Trying another replica.");
                                // Not tracking the source as a machine with bad reputation, because it is possible that we provided the wrong size.

                                continue;
                            }

                            PutResult putResult = await handleCopyAsync((copyFileResult, tempLocation, attemptCount));

                            if (putResult.Succeeded)
                            {
                                // The put succeeded, but this doesn't necessarily mean that we put the content we intended. Check the content hash
                                // to ensure it's what is expected. This should only go wrong for a small portion of non-trusted puts.
                                if (putResult.ContentHash != hashInfo.ContentHash)
                                {
                                    lastErrorMessage =
                                        $"Contenthash at location {location} has contenthash {putResult.ContentHash.ToShortString()} mismatch from {hashInfo.ContentHash.ToShortString()}";
                                    // If PutFileAsync re-hashed the file, then it could have found a content hash which differs from the expected content hash.
                                    // If this happens, we should fail this copy and move to the next location.
                                    Tracer.Warning(
                                        context,
                                        $"{AttemptTracePrefix(attemptCount)} {lastErrorMessage}");
                                    badContentLocations.Add(location);
                                    continue;
                                }

                                if (!putResult.ContentAlreadyExistsInCache)
                                {
                                    // Don't delete the temporary file! It no longer exists after the Put moved it into the cache
                                    deleteTempFile = false;
                                }

                                // Successful case
                                return (result: putResult, retry: false);
                            }
                            else if (putResult.IsCancelled)
                            {
                                return reportCancellationRequested();
                            }
                            else
                            {
                                // Nothing is known about the put's failure. Give up on all locations, do not retry.
                                // An example of a failure requiring this: Failed to reserve space for content
                                var errorMessage = $"Put file for content hash {hashInfo.ContentHash.ToShortString()} failed with error {putResult.ErrorMessage} ";
                                Tracer.Warning(
                                    context,
                                    $"{AttemptTracePrefix(attemptCount)} {errorMessage} diagnostics {putResult.Diagnostics}");
                                return (result: putResult, retry: false);
                            }
                        }
                    }
                }
                finally
                {
                    // If the replicaIndex hasn't been tried before it won't have a value in lastFailureTimes so add it.
                    // Otherwise replace the old failure time with the current time.
                    if (lastFailureTimes.Count <= replicaIndex)
                    {
                        lastFailureTimes.Add(DateTime.Now);
                    }
                    else
                    {
                        lastFailureTimes[replicaIndex] = DateTime.Now;
                    }

                    if (deleteTempFile)
                    {
                        _fileSystem.DeleteFile(tempLocation);
                    }
                }
            }

            if (lastErrorMessage != null)
            {
                lastErrorMessage = ". " + lastErrorMessage;
            }

            return (new PutResult(hashInfo.ContentHash, $"Unable to copy file{lastErrorMessage}"), retry: true);
        }

        private async Task<CopyFileResult> CopyFileAsync(
            Context context,
            T location,
            AbsolutePath tempDestinationPath,
            ContentHashWithSizeAndLocations hashInfo,
            CancellationToken cts)
        {
            try
            {
                // If the file satisfy trusted hash file size boundary, then we hash during the copy (i.e. now) and won't hash when placing the file into the store.
                // Otherwise we don't hash it now and the store will hash the file during put.
                if (_settings.UseTrustedHash(hashInfo.Size))
                {
                    // If we know that the file is large, then hash concurrently from the start
                    bool hashEntireFileConcurrently = _settings.ParallelHashingFileSizeBoundary >= 0 && hashInfo.Size > _settings.ParallelHashingFileSizeBoundary;

                    int bufferSize = GetBufferSize(hashInfo);

                    // Since this is the only place where we hash the file during trusted copies, we attempt to get access to the bytes here,
                    //  to avoid an additional IO operation later. In case that the file is bigger than the ContentLocationStore permits or blobs
                    //  aren't supported, disposing the FileStream twice does not throw or cause issues.
                    using (Stream fileStream = await _fileSystem.OpenAsync(tempDestinationPath, FileAccess.Write, FileMode.Create, FileShare.Read | FileShare.Delete, FileOptions.SequentialScan, bufferSize))
                    using (Stream possiblyRecordingStream = _contentLocationStore.AreBlobsSupported && hashInfo.Size <= _contentLocationStore.MaxBlobSize && hashInfo.Size >= 0 ? (Stream)RecordingStream.WriteRecordingStream(fileStream) : fileStream)
                    using (HashingStream hashingStream = ContentHashers.Get(hashInfo.ContentHash.HashType).CreateWriteHashingStream(possiblyRecordingStream, hashEntireFileConcurrently ? 1 : _settings.ParallelHashingFileSizeBoundary))
                    {
                        var copyFileResult = await _remoteFileCopier.CopyToWithOperationContextAsync(new OperationContext(context, cts), location, hashingStream, hashInfo.Size);
                        copyFileResult.TimeSpentHashing = hashingStream.TimeSpentHashing;

                        if (copyFileResult.Succeeded)
                        {
                            var foundHash = hashingStream.GetContentHash();
                            if (foundHash != hashInfo.ContentHash)
                            {
                                return new CopyFileResult(CopyFileResult.ResultCode.InvalidHash, $"{nameof(CopyFileAsync)} unsuccessful with different hash. Found {foundHash.ToShortString()}, expected {hashInfo.ContentHash.ToShortString()}. Found size {hashingStream.Length}, expected size {hashInfo.Size}.");
                            }

                            // Expose the bytes that were copied, so that small files can be put into the ContentLocationStore even when trusted copy is done
                            if (possiblyRecordingStream is RecordingStream recordingStream)
                            {
                                copyFileResult.BytesFromTrustedCopy = recordingStream.RecordedBytes;
                            }

                            return copyFileResult;
                        }
                        else
                        {
                            // This result will be logged in the caller
                            return copyFileResult;
                        }
                    }
                }
                else
                {
                    return await CopyFileAsync(_remoteFileCopier, location, tempDestinationPath, hashInfo.Size, overwrite: true, cancellationToken: cts);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is IOException)
            {
                // Auth errors are considered errors with the destination.
                // Since FSServer now returns HTTP based exceptions for web, IO failures must be local file paths.
                return new CopyFileResult(CopyFileResult.ResultCode.DestinationPathError, ex, ex.ToString());
            }
            catch (Exception ex)
            {
                // any other exceptions are assumed to be bad remote files.
                return new CopyFileResult(CopyFileResult.ResultCode.SourcePathError, ex, ex.ToString());
            }
        }

        /// <summary>
        /// Override for testing.
        /// </summary>
        protected virtual async Task<CopyFileResult> CopyFileAsync(IFileCopier<T> copier, T sourcePath, AbsolutePath destinationPath, long expectedContentSize, bool overwrite, CancellationToken cancellationToken)
        {
            const int DefaultBufferSize = 1024 * 80;

            if (!overwrite && File.Exists(destinationPath.Path))
            {
                return new CopyFileResult(
                        CopyFileResult.ResultCode.DestinationPathError,
                        $"Destination file {destinationPath} exists but overwrite not specified.");
            }

            var directoryPath = destinationPath.Parent.Path;
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            using var stream = new FileStream(destinationPath.Path, FileMode.Create, FileAccess.Write, FileShare.None, DefaultBufferSize, FileOptions.SequentialScan);
            return await copier.CopyToAsync(sourcePath, stream, expectedContentSize, cancellationToken);
        }

        private static int GetBufferSize(ContentHashWithSizeAndLocations hashInfo)
        {
            // For "small" files we use "small buffer size"
            // For files in [small, large) range we use file.Size
            // For "large" files we use "large buffer size".

            var size = hashInfo.Size;
            if (size <= DefaultSmallBufferSize)
            {
                return DefaultSmallBufferSize;
            }
            else if (size >= DefaultLargeBufferSize)
            {
                return DefaultLargeBufferSize;
            }
            else
            {
                return (int)size;
            }
        }

        private string AttemptTracePrefix(int attemptCount)
        {
            return $"Attempt #{attemptCount}:";
        }

        // This class is used in this type to pass results out of the VerifyRemote method.
        internal class VerifyResult
        {
            public ContentHash Hash { get; set; }

            public IReadOnlyList<MachineLocation> Present { get; set; }

            public IReadOnlyList<MachineLocation> Absent { get; set; }

            public IReadOnlyList<MachineLocation> Unknown { get; set; }
        }

        // Given a content record set, check all the locations and determine, for each location, whether the file is actually
        // present, actually absent, or if its presence or absence cannot be determined in the alloted time.
        // The CheckFileExistsAsync method that is called in this implementation may be doing more complicated stuff (retries, queuing,
        // throttling, its own timeout) than we want or expect; we should dig into this.
        internal async Task<VerifyResult> VerifyAsync(Context context, ContentHashWithSizeAndLocations remote, CancellationToken cancel)
        {
            Contract.Requires(remote != null);

            Task<FileExistenceResult>[] verifications = new Task<FileExistenceResult>[remote.Locations.Count];
            using (var timeoutCancelSource = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                for (int i = 0; i < verifications.Length; i++)
                {
                    T location = _pathTransformer.GeneratePath(remote.ContentHash, remote.Locations[i].Data);
                    var verification = Task.Run(async () => await GatedCheckFileExistenceAsync(location, timeoutCancelSource.Token));
                    verifications[i] = verification;
                }

                // Spend up to the timeout doing as many verification as we can.
                timeoutCancelSource.CancelAfter(VerifyTimeout);

                // In order to await the end of the verifications and still not throw an exception if verification were canceled (or faulted internally), we
                // use the trick of awaiting a WhenAny, which never throws but instead always runs to completion when the argument tasks complete.
#pragma warning disable EPC13 // Suspiciously unobserved result.
                await Task.WhenAny(Task.WhenAll(verifications));
#pragma warning restore EPC13 // Suspiciously unobserved result.
            }

            // Read out the results of the file existence checks
            var present = new List<MachineLocation>();
            var absent = new List<MachineLocation>();
            var unknown = new List<MachineLocation>();
            for (int i = 0; i < verifications.Length; i++)
            {
                var location = remote.Locations[i];
                Task<FileExistenceResult> verification = verifications[i];
                Contract.Assert(verification.IsCompleted);
                if (verification.IsCanceled && !cancel.IsCancellationRequested)
                {
                    Tracer.Info(context, $"During verification, hash {remote.ContentHash.ToShortString()} timed out for location {location}.");
                    unknown.Add(location);
                }
                else if (verification.IsFaulted)
                {
                    Tracer.Info(context, $"During verification, hash {remote.ContentHash.ToShortString()} encountered the error {verification.Exception} while verifying location {location}.");
                    unknown.Add(location);
                }
                else
                {
                    FileExistenceResult result = await verification;
                    if (result.Code == FileExistenceResult.ResultCode.FileExists)
                    {
                        present.Add(location);
                    }
                    else if (result.Code == FileExistenceResult.ResultCode.FileNotFound)
                    {
                        Tracer.Info(context, $"During verification, hash {remote.ContentHash.ToShortString()} was not found at location {location}.");
                        absent.Add(location);
                    }
                    else
                    {
                        unknown.Add(location);
                    }
                }
            }

            return new VerifyResult() { Hash = remote.ContentHash, Present = present, Absent = absent, Unknown = unknown };
        }

        /// <summary>
        /// This gated method attempts to limit the number of simultaneous off-machine file IO.
        /// It's not clear whether this is really a good idea, since usually IO thread management is best left to the scheduler.
        /// But if there is a truly enormous amount of external IO, it's not clear that they will all be scheduled in a way
        /// that will minimize timeouts, so we will try this gate.
        /// </summary>
        private Task<FileExistenceResult> GatedCheckFileExistenceAsync(T path, CancellationToken token)
        {
            return _ioGate.GatedOperationAsync(
                (_) => _remoteFileExistenceChecker.CheckFileExistsAsync(path, Timeout.InfiniteTimeSpan, token),
                token);
        }

        /// <nodoc />
        public CounterSet GetCounters() => _counters.ToCounterSet();

        private enum DistributedContentCopierCounters
        {
            /// <nodoc />
            [CounterType(CounterType.Stopwatch)]
            RemoteCopyFile,

            /// <nodoc />
            RemoteBytes,

            /// <nodoc />
            RemoteFilesCopied,

            /// <nodoc />
            RemoteFilesFailedCopy
        }
    }
}
