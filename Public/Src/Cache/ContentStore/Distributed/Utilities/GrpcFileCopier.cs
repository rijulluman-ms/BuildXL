// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Interfaces.Utils;
using BuildXL.Cache.ContentStore.Service.Grpc;
using BuildXL.Cache.ContentStore.Tracing.Internal;

namespace BuildXL.Cache.ContentStore.Distributed.Utilities
{
    /// <summary>
    /// File copier which operates over Grpc. <seealso cref="GrpcCopyClient"/>
    /// </summary>
    public class GrpcFileCopier : ITraceableAbsolutePathFileCopier, IProactiveCopier
    {
        private readonly Context _context;
        private readonly int _grpcPort;
        private readonly bool _useCompression;

        private readonly GrpcCopyClientCache _clientCache;

        /// <summary>
        /// Constructor for <see cref="GrpcFileCopier"/>.
        /// </summary>
        public GrpcFileCopier(Context context, int grpcPort, int maxGrpcClientCount, int maxGrpcClientAgeMinutes, int grpcClientCleanupDelayMinutes, bool useCompression = false, int? bufferSize = null)
        {
            _context = context;
            _grpcPort = grpcPort;
            _useCompression = useCompression;

            _clientCache = new GrpcCopyClientCache(context, maxGrpcClientCount, maxGrpcClientAgeMinutes, grpcClientCleanupDelayMinutes, bufferSize);
        }

        /// <inheritdoc />
        public async Task<FileExistenceResult> CheckFileExistsAsync(AbsolutePath path, TimeSpan timeout, CancellationToken cancellationToken)
        {
            // Extract host and contentHash from sourcePath
            (string host, ContentHash contentHash) = ExtractHostHashFromAbsolutePath(path);

            using var clientWrapper = await _clientCache.CreateAsync(host, _grpcPort, _useCompression);
            return await clientWrapper.Value.CheckFileExistsAsync(_context, contentHash);
        }

        private (string host, ContentHash contentHash) ExtractHostHashFromAbsolutePath(AbsolutePath sourcePath)
        {
            // TODO: Keep the segments in the AbsolutePath object?
            // TODO: Indexable structure?
            var segments = sourcePath.GetSegments();
            Contract.Assert(segments.Count >= 4);

            var host = segments.First();
            var hashLiteral = segments.Last();
            if (hashLiteral.EndsWith(GrpcDistributedPathTransformer.BlobFileExtension, StringComparison.OrdinalIgnoreCase))
            {
                hashLiteral = hashLiteral.Substring(0, hashLiteral.Length - GrpcDistributedPathTransformer.BlobFileExtension.Length);
            }
            var hashTypeLiteral = segments.ElementAt(segments.Count - 1 - 2);

            if (!Enum.TryParse(hashTypeLiteral, ignoreCase: true, out HashType hashType))
            {
                throw new InvalidOperationException($"{hashTypeLiteral} is not a valid member of {nameof(HashType)}");
            }

            var contentHash = new ContentHash(hashType, HexUtilities.HexToBytes(hashLiteral));

            return (host, contentHash);
        }

        /// <inheritdoc />
        public Task<CopyFileResult> CopyToAsync(AbsolutePath sourcePath, Stream destinationStream, long expectedContentSize, CancellationToken cancellationToken)
        {
            return CopyToAsync(new OperationContext(_context, cancellationToken), sourcePath, destinationStream, expectedContentSize);
        }

        /// <inheritdoc />
        public async Task<CopyFileResult> CopyToAsync(OperationContext context, AbsolutePath sourcePath, Stream destinationStream, long expectedContentSize)
        {
            // Extract host and contentHash from sourcePath
            (string host, ContentHash contentHash) = ExtractHostHashFromAbsolutePath(sourcePath);

            // Contact hard-coded port on source
            using var clientWrapper = await _clientCache.CreateAsync(host, _grpcPort, _useCompression);
            return await clientWrapper.Value.CopyToAsync(context, contentHash, destinationStream, context.Token);
        }

        /// <inheritdoc />
        public async Task<BoolResult> PushFileAsync(OperationContext context, ContentHash hash, Func<Task<Stream>> source, MachineLocation targetMachine)
        {
            var targetPath = new AbsolutePath(targetMachine.Path);
            var targetMachineName = targetPath.IsLocal ? "localhost" : targetPath.GetSegments()[0];

            using var clientWrapper = await _clientCache.CreateAsync(targetMachineName, _grpcPort, _useCompression);
            return await clientWrapper.Value.PushFileAsync(context, hash, source);
        }

        /// <inheritdoc />
        public async Task<BoolResult> RequestCopyFileAsync(OperationContext context, ContentHash hash, MachineLocation targetMachine)
        {
            var targetPath = new AbsolutePath(targetMachine.Path);
            var targetMachineName = targetPath.IsLocal ? "localhost" : targetPath.GetSegments()[0];

            using var clientWrapper = await _clientCache.CreateAsync(targetMachineName, _grpcPort, _useCompression);
            return await clientWrapper.Value.RequestCopyFileAsync(context, hash);
        }
    }
}
