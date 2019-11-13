// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;
using BuildXL.Cache.ContentStore.Distributed.NuCache.InMemory;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;

namespace BuildXL.Cache.ContentStore.Distributed.NuCache
{
    /// <summary>
    /// Configuration type for <see cref="ContentLocationDatabase"/> family of types.
    /// </summary>
    public abstract class ContentLocationDatabaseConfiguration
    {
        /// <summary>
        /// The touch interval for database entries.
        /// NOTE: This is set internally to the same value as <see cref="LocalLocationStoreConfiguration.TouchFrequency"/>
        /// </summary>
        internal TimeSpan TouchFrequency { get; set; }

        /// <summary>
        /// Interval between garbage collecting unreferenced content location entries and metadata in a local database.
        /// </summary>
        public TimeSpan GarbageCollectionInterval { get; set; } = TimeSpan.FromHours(1);

        /// <summary>
        /// Indicates whether reading/writing cluster state from local db is supported.
        /// </summary>
        public bool StoreClusterState { get; set; } = true;

        /// <summary>
        /// When activated, the requests effectively sent to the database will be initally done in memory and later on
        /// flushed to the underlying store.
        /// </summary>
        public bool ContentCacheEnabled { get; set; } = false;

        /// <summary>
        /// Number of threads to use when flushing updates to the underlying storage
        ///
        /// Only useful when <see cref="ContentCacheEnabled"/> is true.
        /// </summary>
        public int FlushDegreeOfParallelism { get; set; } = 1;

        /// <summary>
        /// Number of entries to pool together into a single transaction when doing multithreaded transactional flush.
        /// </summary>
        public int FlushTransactionSize { get; set; } = 100_000;

        /// <summary>
        /// Whether to use a single transaction to the underlying store when flushing instead of one transaction per
        /// change.
        ///
        /// When this setting is on, there is no parallelism done, regardless of
        /// <see cref="FlushDegreeOfParallelism"/>.
        ///
        /// Only useful when <see cref="ContentCacheEnabled"/> is true.
        /// </summary>
        public bool FlushSingleTransaction { get; set; } = true;

        /// <summary>
        /// Percentage of records to maintain in memory after flush
        ///
        /// Only useful when <see cref="ContentCacheEnabled"/> is true.
        /// </summary>
        public double FlushPreservePercentInMemory = 0.5;

        /// <summary>
        /// The maximum number of updates that we are willing to perform in memory before flushing.
        ///
        /// Only useful when <see cref="ContentCacheEnabled"/> is true.
        /// </summary>
        public int CacheMaximumUpdatesPerFlush { get; set; } = 2_500_000;

        /// <summary>
        /// The maximum amount of time that can pass without a flush.
        ///
        /// Only useful when <see cref="ContentCacheEnabled"/> is true.
        /// </summary>
        public TimeSpan CacheFlushingMaximumInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Whether to enable garbage collection of metadata
        /// </summary>
        public bool MetadataGarbageCollectionEnabled { get; set; } = false;

        /// <summary>
        /// Maximum number of metadata entries to keep after garbage collection.
        ///
        /// Only useful when <see cref="MetadataGarbageCollectionEnabled"/> is true.
        /// </summary>
        /// <remarks>
        /// Default is the same as in the SQLiteMemoizationStore
        /// </remarks>
        public int MetadataGarbageCollectionMaximumNumberOfEntriesToKeep { get; set; } = 500_000;

        /// <summary>
        /// Whether to clean the DB when it is corrupted.
        /// </summary>
        /// <remarks>
        /// Should be false for Content, true for Metadata.
        /// </remarks>
        public bool OnFailureDeleteExistingStoreAndRetry { get; set; } = false;
    }

    /// <summary>
    /// Configuration type for <see cref="MemoryContentLocationDatabase"/>.
    /// </summary>
    public sealed class MemoryContentLocationDatabaseConfiguration : ContentLocationDatabaseConfiguration
    {
        // No members. This is a marker type 
    }

    /// <summary>
    /// Configuration type for <see cref="RocksDbContentLocationDatabase"/>.
    /// </summary>
    public sealed class RocksDbContentLocationDatabaseConfiguration : ContentLocationDatabaseConfiguration
    {
        /// <inheritdoc />
        public RocksDbContentLocationDatabaseConfiguration(AbsolutePath storeLocation)
        {
            StoreLocation = storeLocation;
        }

        /// <summary>
        /// The directory containing the key-value store.
        /// </summary>
        public AbsolutePath StoreLocation { get; }

        /// <summary>
        /// Testing purposes only. Used to set location to load initial database data from.
        /// NOTE: This disables <see cref="CleanOnInitialize"/>
        /// </summary>
        public AbsolutePath TestInitialCheckpointPath { get; set; }

        /// <summary>
        /// Gets whether the database is cleared on initialization. (Defaults to true because the standard use case involves restoring from checkpoint after initialization)
        /// </summary>
        public bool CleanOnInitialize { get; set; } = true;

        /// <summary>
        /// Time between full range compactions. These help keep the size of the DB instance down to a minimum.
        /// 
        /// Required because of our workload tends to generate a lot of short-lived entries, which clutter the deeper
        /// levels of the RocksDB LSM tree.
        /// </summary>
        public TimeSpan FullRangeCompactionInterval { get; set; } = Timeout.InfiniteTimeSpan;
    }
}
