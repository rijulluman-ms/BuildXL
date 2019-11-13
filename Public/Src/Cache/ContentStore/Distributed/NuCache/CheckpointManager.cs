// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.NuCache.EventStreaming;
using BuildXL.Cache.ContentStore.Distributed.Redis;
using BuildXL.Cache.ContentStore.Extensions;
using BuildXL.Cache.ContentStore.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Native.IO;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.ParallelAlgorithms;
using BuildXL.Utilities.Tracing;

namespace BuildXL.Cache.ContentStore.Distributed.NuCache
{
    /// <summary>
    /// Helper class responsible for creating and restoring checkpoints of a local database.
    /// </summary>
    internal sealed class CheckpointManager
    {
        private readonly Tracer _tracer = new Tracer(nameof(CheckpointManager));

        private const string CheckpointSizeMetricName = "CheckpointSizeBytes";
        private const string IncrementalCheckpointIdSuffix = "|Incremental";
        private const string CheckpointInfoKey = "CheckpointManager.CheckpointState";

        private const string IncrementalCheckpointInfoEntrySeparator = "\n";

        private readonly ContentLocationDatabase _database;
        private readonly ICheckpointRegistry _checkpointRegistry;
        private readonly CentralStorage _storage;
        private readonly IAbsFileSystem _fileSystem;
        private readonly AbsolutePath _checkpointStagingDirectory;
        private readonly AbsolutePath _incrementalCheckpointDirectory;
        private readonly AbsolutePath _incrementalCheckpointInfoFile;

        private CounterCollection<ContentLocationStoreCounters> Counters { get; }

        private readonly CheckpointConfiguration _configuration;

        /// <summary>
        /// Maps file name to storage id for the currently downloaded checkpoint
        /// </summary>
        private IReadOnlyDictionary<string, string> _incrementalCheckpointInfo = CollectionUtilities.EmptyDictionary<string, string>();

        /// <inheritdoc />
        public CheckpointManager(
            ContentLocationDatabase database,
            ICheckpointRegistry checkpointRegistry,
            CentralStorage storage,
            CheckpointConfiguration configuration,
            CounterCollection<ContentLocationStoreCounters> counters)
        {
            _database = database;
            _checkpointRegistry = checkpointRegistry;
            _storage = storage;
            _configuration = configuration;
            _fileSystem = new PassThroughFileSystem();
            _checkpointStagingDirectory = configuration.WorkingDirectory / "staging";
            _incrementalCheckpointDirectory = configuration.WorkingDirectory / "incremental";
            _fileSystem.CreateDirectory(_incrementalCheckpointDirectory);

            _incrementalCheckpointInfoFile = _incrementalCheckpointDirectory / "checkpointInfo.txt";
            Counters = counters;
        }

        /// <summary>
        /// Creates a checkpoint for a given sequence point.
        /// </summary>
        public Task<BoolResult> CreateCheckpointAsync(OperationContext context, EventSequencePoint sequencePoint)
        {
            context = context.CreateNested();

            string checkpointId = "Unknown";
            long checkpointSize = 0;
            return context.PerformOperationAsync(
                _tracer,
                async () =>
                {
                    bool successfullyUpdatedIncrementalState = false;
                    try
                    {
                        // Creating a working temporary directory
                        using (new DisposableDirectory(_fileSystem, _checkpointStagingDirectory))
                        {
                            // NOTE(jubayard): this needs to be done previous to checkpointing, because we always
                            // fetch the latest version's size in this way. This implies there may be some difference
                            // between the reported value and the actual size on disk: updates will get in in-between.
                            // The better alternative is to actually open the checkpoint and ask, but it seems like too
                            // much.
                            checkpointSize = _database.GetContentDatabaseSizeBytes().GetValueOrDefault(-1);


                            // Saving checkpoint for the database into the temporary folder
                            _database.SaveCheckpoint(context, _checkpointStagingDirectory).ThrowIfFailure();

                            if (_configuration.UseIncrementalCheckpointing)
                            {
                                 checkpointId = await CreateCheckpointIncrementalAsync(context, sequencePoint);
                                 successfullyUpdatedIncrementalState = true;
                            }
                            else
                            {
                                checkpointId = await CreateFullCheckpointAsync(context, sequencePoint);
                            }

                            return BoolResult.Success;
                        }
                    }
                    finally
                    {
                        ClearIncrementalCheckpointStateIfNeeded(context, successfullyUpdatedIncrementalState);
                    }
                },
                extraStartMessage: $"SequencePoint=[{sequencePoint}]",
                extraEndMessage: result => $"SequencePoint=[{sequencePoint}] Id=[{checkpointId}] SizeMb=[{(checkpointSize < 0 ? checkpointSize:checkpointSize*1e-6)}]");
        }

        private async Task<string> CreateFullCheckpointAsync(OperationContext context, EventSequencePoint sequencePoint)
        {
            // Zipping the checkpoint
            var targetZipFile = _checkpointStagingDirectory + ".zip";
            File.Delete(targetZipFile);
            ZipFile.CreateFromDirectory(_checkpointStagingDirectory.ToString(), targetZipFile);

            // Track checkpoint size
            var fileInfo = new System.IO.FileInfo(targetZipFile);
            _tracer.TrackMetric(context, CheckpointSizeMetricName, fileInfo.Length);

            var checkpointBlobName = $"checkpoints/{sequencePoint.SequenceNumber}.{Guid.NewGuid()}.zip";
            var checkpointId = await _storage.UploadFileAsync(context, new AbsolutePath(targetZipFile), checkpointBlobName, garbageCollect: true).ThrowIfFailureAsync();

            // Uploading the checkpoint
            await _checkpointRegistry.RegisterCheckpointAsync(context, checkpointId, sequencePoint).ThrowIfFailure();

            return checkpointId;
        }

        private async Task<string> CreateCheckpointIncrementalAsync(OperationContext context, EventSequencePoint sequencePoint)
        {
            InitializeIncrementalCheckpointIfNeeded(restoring: false);

            var incrementalCheckpointsPrefix = $"incrementalCheckpoints/{sequencePoint.SequenceNumber}.{Guid.NewGuid()}.";
            // See the comment of _incrementalCheckpointInfo for the meaning of keys and values.
            var newCheckpointInfo = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Get files in checkpoint and apply changes to the incremental checkpoint directory (locally and in blob storage)
            var files = _fileSystem.EnumerateFiles(_checkpointStagingDirectory, EnumerateOptions.Recurse).Select(s => s.FullPath).ToList();
            await UploadFilesAsync(context, files, newCheckpointInfo, incrementalCheckpointsPrefix);

            // Finalize by writing the checkpoint info into the incremental checkpoint directory and updating checkpoint registry and storage
            WriteCheckpointInfo(_incrementalCheckpointInfoFile, newCheckpointInfo);

            var checkpointId = await _storage.UploadFileAsync(context, _incrementalCheckpointInfoFile, incrementalCheckpointsPrefix + _incrementalCheckpointInfoFile.FileName, garbageCollect: true).ThrowIfFailureAsync();

            // Add incremental suffix so consumer knows that the checkpoint is an incremental checkpoint
            checkpointId += IncrementalCheckpointIdSuffix;

            await _checkpointRegistry.RegisterCheckpointAsync(context, checkpointId, sequencePoint).ThrowIfFailure();

            // Have to create a dictionary in .NET 4.5.1 because ConcurrentDictionary does not implement IReadOnlyDictionary there.
            UpdateIncrementalCheckpointInfo(new Dictionary<string, string>(newCheckpointInfo));

            return checkpointId;
        }

        private async Task UploadFilesAsync(OperationContext context, List<AbsolutePath> files, ConcurrentDictionary<string, string> newCheckpointInfo, string incrementalCheckpointsPrefix)
        {
            if (_configuration.IncrementalCheckpointDegreeOfParallelism <= 1)
            {
                foreach (var file in files)
                {
                    await UploadOrTouchFileAsync(context, file, newCheckpointInfo, incrementalCheckpointsPrefix);
                }
            }
            else
            {
                await ParallelAlgorithms.WhenDoneAsync(
                    _configuration.IncrementalCheckpointDegreeOfParallelism,
                    context.Token,
                    action: async (addItem, file) =>
                    {
                        // Intentionally using async/await to generate a state machine that will have the current method name in it (to simplify postmortem).
                        await UploadOrTouchFileAsync(context, file, newCheckpointInfo, incrementalCheckpointsPrefix);
                    },
                    items: files.ToArray());
            }
        }

        private async Task UploadOrTouchFileAsync(OperationContext context, AbsolutePath file, ConcurrentDictionary<string, string> newCheckpointInfo, string incrementalCheckpointsPrefix)
        {
            var relativePath = file.Path.Substring(_checkpointStagingDirectory.Path.Length + 1);
            var incrementalCheckpointFile = _incrementalCheckpointDirectory / relativePath;
            if (_incrementalCheckpointInfo.TryGetValue(relativePath, out var storageId) && _database.IsImmutable(file) &&
                _fileSystem.FileExists(incrementalCheckpointFile))
            {
                // File was present in last checkpoint. Just add it to the new incremental checkpoint info
                await _storage.TouchBlobAsync(context, file, storageId, isUploader: true).ThrowIfFailure();
                newCheckpointInfo[relativePath] = storageId;
                Counters[ContentLocationStoreCounters.IncrementalCheckpointFilesUploadSkipped].Increment();
            }
            else
            {
                // File is new or mutable. Need to add to storage and update local incremental checkpoint
                await HardlinkWithFallBackAsync(context, file, incrementalCheckpointFile);

                storageId = await _storage.UploadFileAsync(context, file, incrementalCheckpointsPrefix + file.FileName).ThrowIfFailureAsync();
                newCheckpointInfo[relativePath] = storageId;
                Counters[ContentLocationStoreCounters.IncrementalCheckpointFilesUploaded].Increment();
            }
        }

        private void WriteCheckpointInfo(AbsolutePath path, ConcurrentDictionary<string, string> newCheckpointInfo)
        {
            // Format is newline (IncrementalCheckpointInfoEntrySeparator) separated entries with {Key}={Value}
            File.WriteAllText(path.Path, string.Join(IncrementalCheckpointInfoEntrySeparator, newCheckpointInfo.Select(s => $"{s.Key}={s.Value}")));
        }

        private static Dictionary<string, string> ParseCheckpointInfo(AbsolutePath checkpointFile)
        {
            // Format is newline (IncrementalCheckpointInfoEntrySeparator) separated entries with {Key}={Value}
            return File.ReadAllText(checkpointFile.Path).Split(new[] { IncrementalCheckpointInfoEntrySeparator }, StringSplitOptions.RemoveEmptyEntries)
                                                .Select(entry => entry.Split('=')).ToDictionary(entry => entry[0], entry => entry[1], StringComparer.OrdinalIgnoreCase);
        }

        private void InitializeIncrementalCheckpointIfNeeded(bool restoring)
        {
            // If incremental checkpoint info is not initialized. Clean up incremental checkpoint directory
            // before proceeding

            if (_configuration.UseIncrementalCheckpointing)
            {
                if (_incrementalCheckpointInfo.Count == 0)
                {
                    _fileSystem.CreateDirectory(_incrementalCheckpointDirectory);

                    if (restoring)
                    {
                        // Only RestoreCheckpoint should read the incremental checkpoint file
                        // Thereby, when CreateCheckpoint is not preceded by a RestoreCheckpoint
                        // (i.e. creating checkpoint for new epoch), it will not reuse files

                        if (_fileSystem.FileExists(_incrementalCheckpointInfoFile))
                        {
                            // An incremental checkpoint exists. Make sure that it is loaded
                            _incrementalCheckpointInfo = ParseCheckpointInfo(_incrementalCheckpointInfoFile);
                        }
                    }
                }

                // Synchronize incremental checkpoint directory with incremental checkpoint file
                var files = _fileSystem.EnumerateFiles(_incrementalCheckpointDirectory, EnumerateOptions.Recurse).Select(s => s.FullPath).ToList();
                foreach (var file in files)
                {
                    if (file != _incrementalCheckpointInfoFile)
                    {
                        var relativePath = file.Path.Substring(_incrementalCheckpointDirectory.Path.Length + 1);

                        if (!_incrementalCheckpointInfo.ContainsKey(relativePath))
                        {
                            _fileSystem.DeleteFile(file);
                        }
                    }
                }
            }
        }

        private void ClearIncrementalCheckpointStateIfNeeded(OperationContext context, bool successfullyUpdatedIncrementalState)
        {
            if (!successfullyUpdatedIncrementalState && _configuration.UseIncrementalCheckpointing)
            {
                _tracer.Debug(context, $"Incremental checkpoint state is invalid or corrupted. Deleting '{_incrementalCheckpointInfoFile}'.");
                _incrementalCheckpointInfo = CollectionUtilities.EmptyDictionary<string, string>();
                _fileSystem.DeleteFile(_incrementalCheckpointInfoFile);

                // Clear the latest checkpoint state from the db
                WriteLatestCheckpoint(context, checkpointState: null);
            }
        }

        private void UpdateIncrementalCheckpointInfo(IReadOnlyDictionary<string, string> newCheckpointInfo)
        {
            // Remove extraneous files from local incremental checkpoint
            foreach (var snapshotFileRelativePath in _incrementalCheckpointInfo.Keys)
            {
                if (!newCheckpointInfo.ContainsKey(snapshotFileRelativePath))
                {
                    // Delete any files no longer present in the current snapshot
                    _fileSystem.DeleteFile(_incrementalCheckpointDirectory / snapshotFileRelativePath);
                }
            }

            // Update the in-memory view
            _incrementalCheckpointInfo = newCheckpointInfo;
        }

        private async Task HardlinkWithFallBackAsync(OperationContext context, AbsolutePath source, AbsolutePath target)
        {
            _fileSystem.CreateDirectory(target.Parent);

            var createHardLinkResult = _fileSystem.CreateHardLink(source, target, replaceExisting: true);
            if (createHardLinkResult != CreateHardLinkResult.Success)
            {
                context.TraceDebug($"{_tracer.Name}: Hardlinking {source} to {target} failed: {createHardLinkResult}. Copying...");
                await _fileSystem.CopyFileAsync(source, target, replaceExisting: true);
            }
        }

        /// <summary>
        /// Restores the checkpoint for a given checkpoint id.
        /// </summary>
        public Task<BoolResult> RestoreCheckpointAsync(OperationContext context, CheckpointState checkpointState)
        {
            context = context.CreateNested();
            var checkpointId = checkpointState.CheckpointId;
            return context.PerformOperationAsync(
                _tracer,
                async () =>
                {
                    bool successfullyUpdatedIncrementalState = false;
                    try
                    {
                        bool isIncrementalCheckpoint = false;
                        var checkpointFileExtension = ".zip";
                        if (checkpointId.EndsWith(IncrementalCheckpointIdSuffix, StringComparison.OrdinalIgnoreCase))
                        {
                            isIncrementalCheckpoint = true;
                            checkpointFileExtension = ".txt";
                            // Remove the suffix to get the real checkpoint id used with central storage
                            checkpointId = checkpointId.Substring(0, checkpointId.Length - IncrementalCheckpointIdSuffix.Length);
                        }

                        var checkpointFile = _checkpointStagingDirectory / $"chkpt{checkpointFileExtension}";
                        var extractedCheckpointDirectory = _checkpointStagingDirectory / "chkpt";

                        FileUtilities.DeleteDirectoryContents(_checkpointStagingDirectory.ToString());
                        FileUtilities.DeleteDirectoryContents(extractedCheckpointDirectory.ToString());

                        // Creating a working temporary folder
                        using (new DisposableDirectory(_fileSystem, _checkpointStagingDirectory))
                        {
                            // Getting the checkpoint from the central store
                            await _storage.TryGetFileAsync(context, checkpointId, checkpointFile).ThrowIfFailure();

                            if (isIncrementalCheckpoint)
                            {
                                var incrementalRestoreResult = await RestoreCheckpointIncrementalAsync(context, checkpointFile, extractedCheckpointDirectory);
                                incrementalRestoreResult.ThrowIfFailure();
                            }
                            else
                            {
                                RestoreFullCheckpoint(checkpointFile, extractedCheckpointDirectory);
                            }

                            // Restoring the checkpoint
                            _database.RestoreCheckpoint(context, extractedCheckpointDirectory).ThrowIfFailure();

                            // Save latest checkpoint info to file in case we get restarded and want to know about the previous checkpoint.
                            WriteLatestCheckpoint(context, checkpointState);

                            successfullyUpdatedIncrementalState = true;
                            return BoolResult.Success;
                        }
                    }
                    finally
                    {
                        ClearIncrementalCheckpointStateIfNeeded(context, successfullyUpdatedIncrementalState);
                    }
                },
                extraStartMessage: $"CheckpointId=[{checkpointId}]",
                extraEndMessage: _ => $"CheckpointId=[{checkpointId}]");
        }

        private void WriteLatestCheckpoint(OperationContext context, CheckpointState? checkpointState)
        {
            try
            {
                _database.SetGlobalEntry(CheckpointInfoKey, checkpointState == null ? null : $"{checkpointState.Value.CheckpointId},{checkpointState.Value.CheckpointTime}");
            }
            catch (Exception e)
            {
                _tracer.Warning(context, $"Failed to write latest checkpoint state '{checkpointState?.ToString()}' to database: {e}");
            }
        }

        public (string checkpointId, DateTime checkpointTime)? GetLatestCheckpointInfo(OperationContext context)
        {
            try
            {
                if (_database.TryGetGlobalEntry(CheckpointInfoKey, out var checkpointText))
                {
                    var segments = checkpointText.Split(',');
                    var id = segments[0];
                    var date = DateTime.Parse(segments[1]);
                    return (id, date);
                }
                else
                {
                    return null;
                }

            }
            catch (Exception e)
            {
                _tracer.Debug(context, $"Failed to read latest checkpoint state from disk: {e}");
                return null;
            }
        }

        private static void RestoreFullCheckpoint(AbsolutePath checkpointFile, AbsolutePath extractedCheckpointDirectory)
        {
            // Extracting the checkpoint archive
            ZipFile.ExtractToDirectory(checkpointFile.ToString(), extractedCheckpointDirectory.ToString());
        }

        private Task<BoolResult> RestoreCheckpointIncrementalAsync(OperationContext context, AbsolutePath checkpointFile, AbsolutePath checkpointTargetDirectory)
        {
            return context.PerformOperationAsync(
                _tracer,
                async () =>
                {
                    InitializeIncrementalCheckpointIfNeeded(restoring: true);

                    // Parse the checkpoint info for the checkpoint being restored
                    var newCheckpointInfo = ParseCheckpointInfo(checkpointFile);

                    await RestoreFilesAsync(context, checkpointTargetDirectory, newCheckpointInfo);

                    // Finalize by adding the incremental checkpoint info file to the local incremental checkpoint directory
                    await HardlinkWithFallBackAsync(context, checkpointFile, _incrementalCheckpointInfoFile);
                    UpdateIncrementalCheckpointInfo(newCheckpointInfo);
                    return BoolResult.Success;
                });
        }

        private async Task RestoreFilesAsync(OperationContext context, AbsolutePath checkpointTargetDirectory, Dictionary<string, string> newCheckpointInfo)
        {
            if (_configuration.IncrementalCheckpointDegreeOfParallelism <= 1)
            {
                foreach (var (key, value) in newCheckpointInfo)
                {
                    await RestoreFileAsync(context, checkpointTargetDirectory, key, value).ThrowIfFailure();
                }

            }
            else
            {
                await ParallelAlgorithms.WhenDoneAsync(
                    _configuration.IncrementalCheckpointDegreeOfParallelism,
                    context.Token,
                    action: async (addItem, kvp) =>
                    {
                        var key = kvp.Key;
                        var value = kvp.Value;
                        await RestoreFileAsync(context, checkpointTargetDirectory, key, value).ThrowIfFailure();
                    },
                    items: newCheckpointInfo.ToArray());
            }
        }

        private Task<BoolResult> RestoreFileAsync(OperationContext context, AbsolutePath checkpointTargetDirectory, string relativePath, string storageId)
        {
            return context.PerformOperationAsync(
                _tracer,
                async () =>
                {
                    var incrementalCheckpointFileResult = CreatePath(_incrementalCheckpointDirectory, relativePath);
                    if (!incrementalCheckpointFileResult.Succeeded)
                    {
                        return incrementalCheckpointFileResult;
                    }

                    var incrementalCheckpointFile = incrementalCheckpointFileResult.Value;
                    if ((_incrementalCheckpointInfo.TryGetValue(relativePath, out var fileStorageId)
                            && storageId == fileStorageId)
                        && _database.IsImmutable(incrementalCheckpointFile)
                        && _fileSystem.FileExists(incrementalCheckpointFile))
                    {
                        // File is already present in the incremental checkpoint directory, no need to download it
                        await _storage.TouchBlobAsync(context, incrementalCheckpointFile, storageId, isUploader: false).ThrowIfFailure();
                        Counters[ContentLocationStoreCounters.IncrementalCheckpointFilesDownloadSkipped].Increment();
                    }
                    else
                    {
                        // File is missing, different, or mutable so download it and update it in the incremental checkpoint
                        _fileSystem.DeleteFile(incrementalCheckpointFile);
                        await _storage.TryGetFileAsync(context, storageId, incrementalCheckpointFile).ThrowIfFailure();
                        Counters[ContentLocationStoreCounters.IncrementalCheckpointFilesDownloaded].Increment();
                    }

                    // Move the file from the incremental checkpoint into the extraction directory for loading by the database
                    await HardlinkWithFallBackAsync(context, incrementalCheckpointFile, checkpointTargetDirectory / relativePath);
                    return BoolResult.Success;
                },
                extraStartMessage: relativePath);
        }

        private static Result<AbsolutePath> CreatePath(AbsolutePath basePath, string relativePath)
        {
            try
            {
                // In some cases, the incremental checkpoint state can be corrupted,
                // causing this operation to fail with ArgumentException.
                return basePath / relativePath;
            }
            catch (ArgumentException e) when (e.Message.Contains("Illegal characters in path"))
            {
                return Result.FromErrorMessage<AbsolutePath>($"Illegal characters in path '{relativePath}'.");
            }
        }
    }
}
