// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.FileSystem;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Utilities.Collections;
using ContentStore.Grpc;
using Google.Protobuf;
using Grpc.Core;
using PinRequest = ContentStore.Grpc.PinRequest;

namespace BuildXL.Cache.ContentStore.Service.Grpc
{
    /// <summary>
    /// A CAS server implementation based on GRPC.
    /// </summary>
    public class GrpcContentServer : StartupShutdownSlimBase
    {
        private readonly Tracer _tracer = new Tracer(nameof(GrpcContentServer));

        /// <inheritdoc />
        protected override Tracer Tracer => _tracer;

        private readonly Capabilities _serviceCapabilities;
        private readonly IReadOnlyDictionary<string, IContentStore> _contentStoreByCacheName;
        private readonly int _bufferSize;
        private readonly int _gzipSizeBarrier;
        private readonly ByteArrayPool _pool;

        private readonly IAbsFileSystem _fileSystem;
        private readonly AbsolutePath _workingDirectory;
        private readonly AbsolutePath _tempDirectory;

        /// <summary>
        /// This adapter routes messages from Grpc to the current class.
        /// </summary>
        /// <remarks>
        /// Expected to be read-only after construction. Child classes may overwrite the field in their constructor,
        /// but not afterwards, or behavior will be undefined.
        /// </remarks>
        protected ContentServerAdapter GrpcAdapter { get; set; }

        /// <nodoc />
        protected readonly ILogger Logger;

        private readonly ISessionHandler<IContentSession> _sessionHandler;

        /// <summary>
        /// Session handler for <see cref="IContentSession"/>
        /// </summary>
        /// <remarks>
        /// This is a hack to allow for an <see cref="ISessionHandler"/> with other sessions that inherit from
        /// <see cref="IContentSession"/> to be used instead.
        /// </remarks>
        protected virtual ISessionHandler<IContentSession> ContentSessionHandler => _sessionHandler;

        /// <nodoc />
        public GrpcContentServer(
            ILogger logger,
            Capabilities serviceCapabilities,
            ISessionHandler<IContentSession> sessionHandler,
            IReadOnlyDictionary<string, IContentStore> storesByName,
            LocalServerConfiguration localServerConfiguration = null)
        {
            Contract.Requires(storesByName != null);

            _serviceCapabilities = serviceCapabilities;
            _contentStoreByCacheName = storesByName;
            _bufferSize = localServerConfiguration?.BufferSizeForGrpcCopies ?? ContentStore.Grpc.CopyConstants.DefaultBufferSize;
            _gzipSizeBarrier = localServerConfiguration?.GzipBarrierSizeForGrpcCopies ?? (_bufferSize * 8);
            _pool = new ByteArrayPool(_bufferSize);
            _sessionHandler = sessionHandler;

            _fileSystem = localServerConfiguration?.FileSystem ?? new PassThroughFileSystem();
            _workingDirectory = (localServerConfiguration?.DataRootPath ?? new AbsolutePath(Directory.GetCurrentDirectory())) / "GrpcContentServer";
            _tempDirectory = _workingDirectory / "temp";

            GrpcAdapter = new ContentServerAdapter(this);

            Logger = logger;
        }

        /// <inheritdoc />
        protected override Task<BoolResult> StartupCoreAsync(OperationContext context)
        {
            _fileSystem.CreateDirectory(_tempDirectory);
            return BoolResult.SuccessTask;
        }

        /// <nodoc />
        public ServerServiceDefinition[] Bind() => new ServerServiceDefinition[] { ContentServer.BindService(GrpcAdapter) };

        /// <summary>
        /// Implements a create session request.
        /// </summary>
        public async Task<CreateSessionResponse> CreateSessionAsync(CreateSessionRequest request, CancellationToken token)
        {
            OperationStarted();

            var cacheContext = new Context(new Guid(request.TraceId), Logger);
            var sessionCreationResult = await ContentSessionHandler.CreateSessionAsync(
                new OperationContext(cacheContext, token),
                request.SessionName,
                request.CacheName,
                (ImplicitPin)request.ImplicitPin,
                (Capabilities)request.Capabilities);

            if (sessionCreationResult)
            {
                return new CreateSessionResponse()
                {
                    SessionId = sessionCreationResult.Value.sessionId,
                    TempDirectory = sessionCreationResult.Value.tempDirectory.Path
                };
            }
            else
            {
                return new CreateSessionResponse()
                {
                    ErrorMessage = sessionCreationResult.ErrorMessage
                };
            }
        }
        /// <summary>
        /// Implements a shutdown request for a session.
        /// </summary>
        public async Task<ShutdownResponse> ShutdownSessionAsync(ShutdownRequest request, CancellationToken token)
        {
            var cacheContext = new Context(new Guid(request.Header.TraceId), Logger);
            await ContentSessionHandler.ReleaseSessionAsync(new OperationContext(cacheContext, token), request.Header.SessionId);
            return new ShutdownResponse();
        }

        /// <nodoc />
        private Task<HelloResponse> HelloAsync(HelloRequest request, CancellationToken token)
        {
            OperationStarted();

            return Task.FromResult(
                new HelloResponse
                {
                    Success = true,
                    Capabilities = (int)_serviceCapabilities
                });
        }

        /// <nodoc />
        public async Task<GetStatsResponse> GetStatsAsync(GetStatsRequest request, CancellationToken token)
        {
            var cacheContext = new Context(Guid.NewGuid(), Logger);
            var counters = await ContentSessionHandler.GetStatsAsync(new OperationContext(cacheContext, token));
            if (!counters)
            {
                return GetStatsResponse.Failure();
            }

            return GetStatsResponse.Create(counters.Value.ToDictionaryIntegral());
        }

        /// <summary>
        /// Implements an update tracker request.
        /// TODO: Handle targeting of different stores. (bug 1365340)
        /// </summary>
        private async Task<RemoveFromTrackerResponse> RemoveFromTrackerAsync(
            RemoveFromTrackerRequest request,
            CancellationToken token)
        {
            OperationStarted();

            DateTime startTime = DateTime.UtcNow;
            var cacheContext = new Context(new Guid(request.TraceId), Logger);
            var removeFromTrackerResult = await ContentSessionHandler.RemoveFromTrackerAsync(new OperationContext(cacheContext, token));
            if (!removeFromTrackerResult)
            {
                return new RemoveFromTrackerResponse
                {
                    Header = ResponseHeader.Failure(startTime, removeFromTrackerResult.ErrorMessage, removeFromTrackerResult.Diagnostics)
                };
            }

            long filesEvicted = removeFromTrackerResult.Value;

            return new RemoveFromTrackerResponse
            {
                Header = ResponseHeader.Success(startTime),
                FilesEvicted = filesEvicted
            };
        }

        private async Task<OpenStreamResult> GetFileStreamAsync(Context context, ContentHash hash)
        {
            // Iterate through all known stores, looking for content in each.
            // In most of our configurations there is just one store anyway,
            // and doing this means both we can callers don't have
            // to deal with cache roots and drive letters.

            foreach (KeyValuePair<string, IContentStore> entry in _contentStoreByCacheName)
            {
                if (entry.Value is IStreamStore store)
                {
                    OpenStreamResult result = await store.StreamContentAsync(context, hash);
                    if (result.Code != OpenStreamResult.ResultCode.ContentNotFound)
                    {
                        return result;
                    }
                }
            }

            return new OpenStreamResult(OpenStreamResult.ResultCode.ContentNotFound, $"{hash.ToShortString()} to found");
        }

        private async Task<ExistenceResponse> CheckFileExistsAsync(ExistenceRequest request, CancellationToken token)
        {
            OperationStarted();

            DateTime startTime = DateTime.UtcNow;
            Context cacheContext = new Context(new Guid(request.TraceId), Logger);
            HashType type = (HashType)request.HashType;
            ContentHash hash = request.ContentHash.ToContentHash((HashType)request.HashType);

            // Iterate through all known stores, looking for content in each.
            // In most of our configurations there is just one store anyway,
            // and doing this means both we can callers don't have
            // to deal with cache roots and drive letters.

            foreach (KeyValuePair<string, IContentStore> entry in _contentStoreByCacheName)
            {
                if (entry.Value is IStreamStore store)
                {
                    FileExistenceResult result = await store.CheckFileExistsAsync(cacheContext, hash);
                    if (result.Succeeded)
                    {
                        return new ExistenceResponse { Header = ResponseHeader.Success(startTime) };
                    }
                }
            }

            return new ExistenceResponse { Header = ResponseHeader.Failure(startTime, $"{hash.ToShortString()} not found in the cache") };
        }

        /// <summary>
        /// Implements a copy file request.
        /// </summary>
        private async Task CopyFileAsync(CopyFileRequest request, IServerStreamWriter<CopyFileResponse> responseStream, ServerCallContext context)
        {
            OperationStarted();

            // Get the content stream.
            Context cacheContext = new Context(new Guid(request.TraceId), Logger);
            ContentHash hash = request.GetContentHash();
            OpenStreamResult result = await GetFileStreamAsync(cacheContext, hash);

            // If result is unsuccessful, then result.Stream is null, but using(null) is just a no op.
            using (result.Stream)
            {
                // Figure out response headers.
                CopyCompression compression = CopyCompression.None;
                Metadata headers = new Metadata();
                switch (result.Code)
                {
                    case OpenStreamResult.ResultCode.ContentNotFound:
                        headers.Add("Exception", "ContentNotFound");
                        headers.Add("Message", $"Requested content at {hash} not found.");
                        break;
                    case OpenStreamResult.ResultCode.Error:
                        Contract.Assert(result.Exception != null);
                        headers.Add("Exception", result.Exception.GetType().Name);
                        headers.Add("Message", result.Exception.Message);
                        break;
                    case OpenStreamResult.ResultCode.Success:
                        Contract.Assert(result.Stream != null);
                        long size = result.Stream.Length;
                        headers.Add("FileSize", size.ToString());
                        if ((request.Compression == CopyCompression.Gzip) && (size > _gzipSizeBarrier))
                        {
                            compression = CopyCompression.Gzip;
                        }
                        headers.Add("Compression", compression.ToString());
                        headers.Add("ChunkSize", _bufferSize.ToString());
                        break;
                    default:
                        throw new NotImplementedException($"Unknown result.Code '{result.Code}'.");
                }

                // Send the response headers.
                await context.WriteResponseHeadersAsync(headers);

                // Send the content.
                if (result.Succeeded)
                {
                    var operationContext = new OperationContext(cacheContext, context.CancellationToken);

                    using (var arrayHandle = _pool.Get())
                    {
                        StreamContentDelegate streamContent = compression == CopyCompression.None ? (StreamContentDelegate)StreamContentAsync : StreamContentWithCompressionAsync;

                        byte[] buffer = arrayHandle.Value;
                        await operationContext.PerformOperationAsync(
                                _tracer,
                                () => streamContent(result.Stream, buffer, responseStream, context.CancellationToken),
                                traceOperationStarted: false, // Tracing only stop messages
                                extraEndMessage: r => $"Hash={hash.ToShortString()}, GZip={(compression == CopyCompression.Gzip ? "on" : "off")}.")
                            .IgnoreFailure(); // The error was already logged.
                    }
                }
            }
        }

        /// <summary>
        /// Implements a request copy file request
        /// </summary>
        [SuppressMessage("AsyncUsage", "AsyncFixer02:DisposeAsync should be used instead of openStreamResult.Stream.Dispose")]
        private async Task<RequestCopyFileResponse> RequestCopyFileAsync(RequestCopyFileRequest request, CancellationToken cancellationToken)
        {
            OperationStarted();

            DateTime startTime = DateTime.UtcNow;
            Context cacheContext = new Context(new Guid(request.TraceId), Logger);
            ContentHash hash = request.ContentHash.ToContentHash((HashType)request.HashType);

            // Iterate through all known stores, looking for content in each.
            // In most of our configurations there is just one store anyway,
            // and doing this means both we can callers don't have
            // to deal with cache roots and drive letters.

            if (_contentStoreByCacheName.Values.OfType<ICopyRequestHandler>().FirstOrDefault() is ICopyRequestHandler handler)
            {
                var result = await handler.HandleCopyFileRequestAsync(cacheContext, hash);
                if (result.Succeeded)
                {
                    return new RequestCopyFileResponse { Header = ResponseHeader.Success(startTime) };
                }

                return new RequestCopyFileResponse { Header = ResponseHeader.Failure(startTime, result.ErrorMessage) };

            }

            return new RequestCopyFileResponse { Header = ResponseHeader.Failure(startTime, $"No stores implement {nameof(ICopyRequestHandler)}.") };
        }

        private readonly ConcurrentBigSet<ContentHash> _ongoingPushes = new ConcurrentBigSet<ContentHash>();

        /// <summary>
        /// Handles a request to copy content to this machine.
        /// </summary>
        public async Task HandlePushFileAsync(IAsyncStreamReader<PushFileRequest> requestStream, IServerStreamWriter<PushFileResponse> responseStream, ServerCallContext callContext)
        {
            OperationStarted();

            var startTime = DateTime.UtcNow;

            var pushRequest = PushRequest.FromMetadata(callContext.RequestHeaders);

            var hash = pushRequest.Hash;
            var cacheContext = new Context(pushRequest.TraceId, Logger);
            var token = callContext.CancellationToken;

            var store = _contentStoreByCacheName.Values.OfType<IPushFileHandler>().FirstOrDefault();

            if (store == null)
            {
                Tracer.Debug(cacheContext, $"{nameof(HandlePushFileAsync)}: Copy of {hash.ToShortString()} skipped because no stores implement {nameof(IPushFileHandler)}.");
                await callContext.WriteResponseHeadersAsync(PushResponse.DontCopy.Metadata);
                return;
            }

            if (store.HasContentLocally(cacheContext, hash))
            {
                Tracer.Debug(cacheContext, $"{nameof(HandlePushFileAsync)}: Copy of {hash.ToShortString()} skipped because content is already local.");
                await callContext.WriteResponseHeadersAsync(PushResponse.DontCopy.Metadata);
                return;
            }

            if (!_ongoingPushes.Add(hash))
            {
                Tracer.Debug(cacheContext, $"{nameof(HandlePushFileAsync)}: Copy of {hash.ToShortString()} skipped because another request to push it is already being handled.");
                await callContext.WriteResponseHeadersAsync(PushResponse.DontCopy.Metadata);
                return;
            }

            try
            {
                await callContext.WriteResponseHeadersAsync(PushResponse.Copy.Metadata);

                var tempFilePath = AbsolutePath.CreateRandomFileName(_tempDirectory);

                using (var tempFile = File.OpenWrite(tempFilePath.Path))
                {
                    while (await requestStream.MoveNext())
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        var request = requestStream.Current;
                        var bytes = request.Content.ToByteArray();
                        await tempFile.WriteAsync(bytes, 0, bytes.Length);
                    }
                }

                var result = await store.HandlePushFileAsync(cacheContext, hash, tempFilePath, token);

                File.Delete(tempFilePath.Path);

                var response = result
                    ? new PushFileResponse { Header = ResponseHeader.Success(startTime) }
                    : new PushFileResponse { Header = ResponseHeader.Failure(startTime, result.ErrorMessage) };

                await responseStream.WriteAsync(response);
            }
            finally
            {
                _ongoingPushes.Remove(hash);
            }
        }

        private delegate Task<Result<(long Chunks, long Bytes)>> StreamContentDelegate(Stream input, byte[] buffer, IServerStreamWriter<CopyFileResponse> responseStream, CancellationToken ct);

        private async Task<Result<(long Chunks, long Bytes)>> StreamContentAsync(Stream input, byte[] buffer, IServerStreamWriter<CopyFileResponse> responseStream, CancellationToken ct)
        {
            Contract.Requires(!(input is null));
            Contract.Requires(!(responseStream is null));

            int chunkSize = 0;
            long chunks = 0L;
            long bytes = 0L;

            // Pre-fill buffer with the file's first chunk
            await readNextChunk();

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (chunkSize == 0) { break; }

                ByteString content = ByteString.CopyFrom(buffer, 0, chunkSize);
                CopyFileResponse response = new CopyFileResponse() { Content = content, Index = chunks };

                bytes += chunkSize;
                chunks++;

                // Read the next chunk while waiting for the response
                await Task.WhenAll(readNextChunk(), responseStream.WriteAsync(response));
            }

            return (chunks, bytes);

            async Task<int> readNextChunk() { chunkSize = await input.ReadAsync(buffer, 0, buffer.Length, ct); return chunkSize; }
        }

        private async Task<Result<(long Chunks, long Bytes)>> StreamContentWithCompressionAsync(Stream input, byte[] buffer, IServerStreamWriter<CopyFileResponse> responseStream, CancellationToken ct)
        {
            Contract.Requires(!(input is null));
            Contract.Requires(!(responseStream is null));

            long bytes = 0L;
            long chunks = 0L;
            using (Stream grpcStream = new BufferedWriteStream(
                buffer,
                async (byte[] bf, int offset, int count) =>
                {
                    ByteString content = ByteString.CopyFrom(bf, offset, count);
                    CopyFileResponse response = new CopyFileResponse() { Content = content, Index = chunks };
                    await responseStream.WriteAsync(response);
                    bytes += count;
                    chunks++;
                }
            ))
            {
                using (Stream compressionStream = new GZipStream(grpcStream, System.IO.Compression.CompressionLevel.Fastest, true))
                {
                    await input.CopyToAsync(compressionStream, buffer.Length, ct);
                    await compressionStream.FlushAsync(ct);
                }
                await grpcStream.FlushAsync(ct);
            }

            return (chunks, bytes);
        }

        /// <summary>
        /// Implements a pin request.
        /// </summary>
        private async Task<PinResponse> PinAsync(PinRequest request, CancellationToken token)
        {
            OperationStarted();

            DateTime startTime = DateTime.UtcNow;

            var cacheContext = new Context(new Guid(request.Header.TraceId), Logger);
            return await RunFuncAndReportAsync(
                request.Header.SessionId,
                async session =>
                {
                    PinResult pinResult = await session.PinAsync(
                        cacheContext,
                        request.ContentHash.ToContentHash((HashType)request.HashType),
                        token);
                    return new PinResponse
                    {
                        Header = new ResponseHeader(
                                   startTime, pinResult.Succeeded, (int)pinResult.Code, pinResult.ErrorMessage, pinResult.Diagnostics)
                    };
                },
                errorMessage =>
                    new PinResponse { Header = ResponseHeader.Failure(startTime, (int)PinResult.ResultCode.Error, errorMessage) });
        }

        /// <summary>
        /// Bulk pin content hashes.
        /// </summary>
        private async Task<PinBulkResponse> PinBulkAsync(PinBulkRequest request, CancellationToken token)
        {
            OperationStarted();

            DateTime startTime = DateTime.UtcNow;
            var cacheContext = new Context(new Guid(request.Header.TraceId), Logger);
            return await RunFuncAndReportAsync(
                request.Header.SessionId,
                async session =>
                {
                    var pinList = new List<ContentHash>();
                    foreach (var hash in request.Hashes)
                    {
                        pinList.Add(hash.ContentHash.ToContentHash((HashType)hash.HashType));
                    }

                    List<Task<Indexed<PinResult>>> pinResults = (await session.PinAsync(
                        cacheContext,
                        pinList,
                        token)).ToList();
                    var response = new PinBulkResponse();
                    try
                    {
                        foreach (var pinResult in pinResults)
                        {
                            var result = await pinResult;
                            var responseHeader = new ResponseHeader(
                                startTime,
                                result.Item.Succeeded,
                                (int)result.Item.Code,
                                result.Item.ErrorMessage,
                                result.Item.Diagnostics);
                            response.Header.Add(result.Index, responseHeader);
                        }
                    }
                    catch (Exception)
                    {
                        pinResults.ForEach(task => task.FireAndForget(cacheContext));
                        throw;
                    }

                    return response;
                },
                errorMessage =>
                {
                    var header = ResponseHeader.Failure(startTime, (int)PinResult.ResultCode.Error, errorMessage);
                    var response = new PinBulkResponse();
                    int i = 0;
                    foreach (var hash in request.Hashes)
                    {
                        response.Header.Add(i, header);
                        i++;
                    }
                    return response;
                });
        }

        /// <summary>
        /// Implements a place file request.
        /// </summary>
        private async Task<PlaceFileResponse> PlaceFileAsync(PlaceFileRequest request, CancellationToken token)
        {
            OperationStarted();

            DateTime startTime = DateTime.UtcNow;
            var cacheContext = new Context(new Guid(request.Header.TraceId), Logger);
            return await RunFuncAndReportAsync(
                request.Header.SessionId,
                async session =>
                {
                    PlaceFileResult placeFileResult = await session.PlaceFileAsync(
                        cacheContext,
                        request.ContentHash.ToContentHash((HashType)request.HashType),
                        new AbsolutePath(request.Path),
                        (FileAccessMode)request.FileAccessMode,
                        FileReplacementMode.ReplaceExisting, // Hard-coded because the service can't tell if this is a retry (where the previous try may have left a partial file)
                        (FileRealizationMode)request.FileRealizationMode,
                        token);
                    return new PlaceFileResponse
                    {
                        Header =
                                   new ResponseHeader(
                                       startTime,
                                       placeFileResult.Succeeded,
                                       (int)placeFileResult.Code,
                                       placeFileResult.ErrorMessage,
                                       placeFileResult.Diagnostics),
                        ContentSize = placeFileResult.FileSize
                    };
                },
                errorMessage => new PlaceFileResponse
                {
                    Header = ResponseHeader.Failure(startTime, (int)PlaceFileResult.ResultCode.Error, errorMessage)
                });
        }

        /// <summary>
        /// Implements a put file request.
        /// </summary>
        private async Task<PutFileResponse> PutFileAsync(PutFileRequest request, CancellationToken token)
        {
            OperationStarted();

            DateTime startTime = DateTime.UtcNow;
            var cacheContext = new Context(new Guid(request.Header.TraceId), Logger);
            return await RunFuncAndReportAsync(
                request.Header.SessionId,
                async session =>
                {
                    PutResult putResult;
                    if (request.ContentHash == ByteString.Empty)
                    {
                        putResult = await session.PutFileAsync(
                            cacheContext,
                            (HashType)request.HashType,
                            new AbsolutePath(request.Path),
                            (FileRealizationMode)request.FileRealizationMode,
                            token);
                    }
                    else
                    {
                        putResult = await session.PutFileAsync(
                            cacheContext,
                            request.ContentHash.ToContentHash((HashType)request.HashType),
                            new AbsolutePath(request.Path),
                            (FileRealizationMode)request.FileRealizationMode,
                            token);
                    }

                    return new PutFileResponse
                    {
                        Header =
                                   new ResponseHeader(
                                       startTime,
                                       putResult.Succeeded,
                                       putResult.Succeeded ? 0 : 1,
                                       putResult.ErrorMessage,
                                       putResult.Diagnostics),
                        ContentSize = putResult.ContentSize,
                        ContentHash = putResult.ContentHash.ToByteString(),
                        HashType = (int)putResult.ContentHash.HashType
                    };
                },
                errorMessage => new PutFileResponse { Header = ResponseHeader.Failure(startTime, errorMessage) });
        }

        /// <summary>
        /// Implements a heartbeat request for a session.
        /// </summary>
        private Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CancellationToken token)
        {
            OperationStarted();

            DateTime startTime = DateTime.UtcNow;
            return RunFuncAndReportAsync(
                request.Header.SessionId,
                session => Task.FromResult(new HeartbeatResponse { Header = ResponseHeader.Success(startTime) }),
                errorMessage => new HeartbeatResponse { Header = ResponseHeader.Failure(startTime, errorMessage) });
        }

        private async Task<DeleteContentResponse> DeleteAsync(DeleteContentRequest request, CancellationToken ct)
        {
            OperationStarted();

            DateTime startTime = DateTime.UtcNow;
            var cacheContext = new Context(new Guid(request.TraceId), Logger);
            var contentHash = request.ContentHash.ToContentHash((HashType)request.HashType);

            var deleteResults = await Task.WhenAll<DeleteResult>(_contentStoreByCacheName.Values.Select(store => store.DeleteAsync(cacheContext, contentHash)));

            bool succeeded = true;
            long evictedSize = 0L;
            long pinnedSize = 0L;
            int code = (int)DeleteResult.ResultCode.ContentNotFound;
            foreach (var deleteResult in deleteResults)
            {
                succeeded &= deleteResult.Succeeded;
                evictedSize += deleteResult.EvictedSize;
                pinnedSize += deleteResult.PinnedSize;

                // Return the most severe result code
                code = Math.Max(code, (int)deleteResult.Code);
            }

            return new DeleteContentResponse
            {
                Header = succeeded ? ResponseHeader.Success(startTime) : ResponseHeader.Failure(startTime, string.Join(Environment.NewLine, deleteResults.Select(r => r.ToString()))),
                EvictedSize = evictedSize,
                PinnedSize = pinnedSize,
                Result = code
            };
        }

        private void OperationStarted([CallerMemberName]string requestType = "Unknown")
        {
            // TODO: Add counter, but don't trace
        }

        private async Task<T> RunFuncAndReportAsync<T>(
            int sessionId,
            Func<IContentSession, Task<T>> taskFunc,
            Func<string, T> failFunc)
        {
            if (!ContentSessionHandler.TryGetSession(sessionId, out var session))
            {
                return failFunc($"Could not find session for session ID {sessionId}");
            }

            // TODO ST: the code is polluted with Task.Yield with no comments.
            await Task.Yield();

            try
            {
                return await taskFunc(session);
            }
            catch (TaskCanceledException)
            {
                Logger.Info("GRPC server operation canceled.");
                return failFunc("The operation was canceled.");
            }
            catch (Exception e)
            {
                Logger.Error(e, "GRPC server operation failed.");
                return failFunc(e.ToString());
            }
        }

        /// <summary>
        /// Glue logic between this class and the Grpc abstract class.
        /// </summary>
        /// <remarks>
        /// This adapter only implements the content verbs, and will throw an
        /// unimplemented exception when a client calls an unavailable method.
        /// </remarks>
        protected class ContentServerAdapter : ContentServer.ContentServerBase
        {
            private readonly GrpcContentServer _contentServer;

            /// <inheritdoc />
            public ContentServerAdapter(GrpcContentServer contentServer)
            {
                _contentServer = contentServer;
            }

            /// <inheritdoc />
            public override Task<ExistenceResponse> CheckFileExists(ExistenceRequest request, ServerCallContext context) => _contentServer.CheckFileExistsAsync(request, context.CancellationToken);

            /// <inheritdoc />
            public override Task CopyFile(CopyFileRequest request, IServerStreamWriter<CopyFileResponse> responseStream, ServerCallContext context) => _contentServer.CopyFileAsync(request, responseStream, context);

            /// <inheritdoc />
            public override Task<RequestCopyFileResponse> RequestCopyFile(RequestCopyFileRequest request, ServerCallContext context) => _contentServer.RequestCopyFileAsync(request, context.CancellationToken);

            /// <inheritdoc />
            public override Task PushFile(IAsyncStreamReader<PushFileRequest> requestStream, IServerStreamWriter<PushFileResponse> responseStream, ServerCallContext context) => _contentServer.HandlePushFileAsync(requestStream, responseStream, context);

            /// <inheritdoc />
            public override Task<HelloResponse> Hello(HelloRequest request, ServerCallContext context) => _contentServer.HelloAsync(request, context.CancellationToken);

            /// <inheritdoc />
            public override Task<GetStatsResponse> GetStats(GetStatsRequest request, ServerCallContext context) => _contentServer.GetStatsAsync(request, context.CancellationToken);

            /// <inheritdoc />
            public override Task<CreateSessionResponse> CreateSession(CreateSessionRequest request, ServerCallContext context) => _contentServer.CreateSessionAsync(request, context.CancellationToken);

            /// <inheritdoc />
            public override Task<DeleteContentResponse> Delete(DeleteContentRequest request, ServerCallContext context) => _contentServer.DeleteAsync(request, context.CancellationToken);

            /// <inheritdoc />
            public override Task<PinResponse> Pin(PinRequest request, ServerCallContext context) => _contentServer.PinAsync(request, context.CancellationToken);

            /// <inheritdoc />
            public override Task<PlaceFileResponse> PlaceFile(PlaceFileRequest request, ServerCallContext context) => _contentServer.PlaceFileAsync(request, context.CancellationToken);

            /// <inheritdoc />
            public override Task<PinBulkResponse> PinBulk(PinBulkRequest request, ServerCallContext context) => _contentServer.PinBulkAsync(request, context.CancellationToken);

            /// <inheritdoc />
            public override Task<PutFileResponse> PutFile(PutFileRequest request, ServerCallContext context) => _contentServer.PutFileAsync(request, context.CancellationToken);

            /// <inheritdoc />
            public override Task<ShutdownResponse> ShutdownSession(ShutdownRequest request, ServerCallContext context) => _contentServer.ShutdownSessionAsync(request, context.CancellationToken);

            /// <inheritdoc />
            public override Task<HeartbeatResponse> Heartbeat(HeartbeatRequest request, ServerCallContext context) => _contentServer.HeartbeatAsync(request, context.CancellationToken);

            /// <inheritdoc />
            public override Task<RemoveFromTrackerResponse> RemoveFromTracker(RemoveFromTrackerRequest request, ServerCallContext context) => _contentServer.RemoveFromTrackerAsync(request, context.CancellationToken);
        }
    }
}
