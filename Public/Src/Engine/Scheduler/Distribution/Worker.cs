// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Pips;
using BuildXL.Pips.Operations;
using BuildXL.Scheduler.Artifacts;
using BuildXL.Scheduler.Graph;
using BuildXL.Scheduler.Tracing;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Tasks;
using BuildXL.Utilities.Threading;
using static BuildXL.Utilities.FormattableStringEx;

namespace BuildXL.Scheduler.Distribution
{
    /// <summary>
    /// A local or remote worker which is responsible for executing process and IPC pips
    /// </summary>
    public abstract class Worker : IDisposable
    {
        /// <summary>
        /// Local worker index in the workers' array
        /// </summary>
        public const int LocalWorkerIndex = 0;

        /// <summary>
        /// Name of the RAM semaphore
        /// </summary>
        private const string RamSemaphoreName = "BuildXL.Scheduler.Worker.TotalMemory";

        private const string CommitSemaphoreName = "BuildXL.Scheduler.Worker.TotalCommit";

        /// <summary>
        /// Defines event handler for changes in worker resources
        /// </summary>
        internal delegate void WorkerResourceChangedHandler(Worker worker, WorkerResource resource, bool resourceIncreased);

        private int m_acquiredCacheLookupSlots;
        private int m_acquiredProcessSlots;
        private int m_acquiredIpcSlots;
        private ContentTrackingSet m_availableContent;
        private ContentTrackingSet m_availableHashes;
        private SemaphoreSet<StringId> m_workerSemaphores;
        private readonly WorkerPipStateManager m_workerPipStateManager;
        private WorkerNodeStatus m_status;
        private OperationContext m_workerStatusOperation;
        private OperationContext m_currentStatusOperation;

        /// <summary>
        /// Whether the worker has finished all pending requests after stop is initiated.
        /// </summary>
        protected readonly TaskSourceSlim<bool> DrainCompletion;

        /// <summary>
        /// Lock needed to cover 'acquireslots' and 'earlyrelease' logics
        /// </summary>
        protected readonly ReadWriteLock EarlyReleaseLock = ReadWriteLock.Create();

        /// <summary>
        /// If the worker is released early, we record the datetime.
        /// </summary>
        public DateTime? WorkerEarlyReleasedTime;

        internal static readonly OperationKind WorkerStatusParentOperationKind = OperationKind.Create("Distribution.WorkerStatus");

        internal static readonly ReadOnlyArray<OperationKind> WorkerStatusOperationKinds = EnumTraits<WorkerNodeStatus>.EnumerateValues()
            .Select(status => OperationKind.Create($"{WorkerStatusParentOperationKind.Name}.{status}"))
            .ToReadOnlyArray();

        /// <summary>
        /// Gets or sets whether sufficient resources are available. When set to false, <see cref="EffectiveTotalProcessSlots"/> are throttled to 1 to
        /// prevent further resource exhaustion by scheduling more pips
        /// </summary>
        public bool ResourcesAvailable
        {
            get
            {
                return m_resourcesAvailable;
            }

            set
            {
                var oldValue = m_resourcesAvailable;
                m_resourcesAvailable = value;
                OnWorkerResourcesChanged(WorkerResource.ResourcesAvailable, increased: value && !oldValue);
            }
        }

        private bool m_resourcesAvailable = true;

        /// <summary>
        /// The identifier for the worker.
        /// The local worker always has WorkerId=0
        /// </summary>
        public uint WorkerId { get; }

        /// <summary>
        /// The total amount of slots for process execution (i.e., max degree of pip parallelism). This can
        /// be adjusted due to resource availability. Namely, it will be one if <see cref="ResourcesAvailable"/> is false.
        /// </summary>
        public int EffectiveTotalProcessSlots => ResourcesAvailable ? TotalProcessSlots : 1;

        /// <summary>
        /// The total amount of slots for process execution (i.e., max degree of pip parallelism).
        /// </summary>
        public int TotalProcessSlots
        {
            get
            {
                return Volatile.Read(ref m_totalProcessSlots);
            }

            protected set
            {
                var oldValue = TotalProcessSlots;
                Volatile.Write(ref m_totalProcessSlots, value);
                OnWorkerResourcesChanged(WorkerResource.TotalProcessSlots, value > oldValue);
            }
        }

        private int m_totalProcessSlots;
        private int m_totalCacheLookupSlots;

        /// <summary>
        /// The total amount of slots for cache lookup (i.e., max degree of pip parallelism)
        /// </summary>
        public int TotalCacheLookupSlots
        {
            get
            {
                return Volatile.Read(ref m_totalCacheLookupSlots);
            }

            protected set
            {
                var oldValue = Volatile.Read(ref m_totalCacheLookupSlots);
                Volatile.Write(ref m_totalCacheLookupSlots, value);
                OnWorkerResourcesChanged(WorkerResource.TotalCacheLookupSlots, value > oldValue);
            }
        }

        /// <summary>
        /// Name of the RAM semaphore
        /// </summary>
        private StringId m_ramSemaphoreNameId;

        private int m_ramSemaphoreIndex = -1;

        /// <summary>
        /// The total amount of available ram on the worker at the beginning of the build.
        /// </summary>
        public int? TotalRamMb
        {
            get
            {
                return m_totalMemoryMb;
            }

            set
            {
                var oldValue = m_totalMemoryMb;
                m_totalMemoryMb = value;
                OnWorkerResourcesChanged(WorkerResource.AvailableMemoryMb, increased: value > oldValue);
            }
        }

        private int? m_totalMemoryMb;

        /// <summary>
        /// The total amount of available memory on the worker during the build.
        /// </summary>
        public int? ActualFreeMemoryMb;

        /// <summary>
        /// Name of the RAM semaphore
        /// </summary>
        private StringId m_commitSemaphoreNameId;

        private int m_commitSemaphoreIndex = -1;

        /// <summary>
        /// The total amount of available commit on the worker at the beginning of the build.
        /// </summary>
        public int? TotalCommitMb
        {
            get
            {
                return m_totalCommitMb;
            }

            set
            {
                var oldValue = m_totalCommitMb;
                m_totalCommitMb = value;
                OnWorkerResourcesChanged(WorkerResource.AvailableCommitMb, increased: value > oldValue);
            }
        }

        private int? m_totalCommitMb;

        /// <summary>
        /// The total amount of available commit on the worker during the build.
        /// </summary>
        public int? ActualFreeCommitMb;

        /// <summary>
        /// Gets the estimate RAM usage on the machine
        /// </summary>
        public int EstimatedFreeRamMb
        {
            get
            {
                if (TotalRamMb == null || m_ramSemaphoreIndex < 0)
                {
                    return 0;
                }

                var availablePercentFactor = ProcessExtensions.PercentageResourceLimit - m_workerSemaphores.GetUsage(m_ramSemaphoreIndex);

                return (int)(((long)availablePercentFactor * TotalRamMb.Value) / ProcessExtensions.PercentageResourceLimit);
            }
        }

        /// <summary>
        /// Gets the estimate RAM usage on the machine
        /// </summary>
        public int EstimatedFreeCommitMb
        {
            get
            {
                if (TotalCommitMb == null || m_ramSemaphoreIndex < 0)
                {
                    return 0;
                }

                var availablePercentFactor = ProcessExtensions.PercentageResourceLimit - m_workerSemaphores.GetUsage(m_ramSemaphoreIndex);

                return (int)(((long)availablePercentFactor * TotalRamMb.Value) / ProcessExtensions.PercentageResourceLimit);
            }
        }

        /// <summary>
        /// Default memory usage for process pips in case of no historical ram usage info 
        /// </summary>
        /// <remarks>
        /// If there is no historical ram usage for the process pips, we assume that 80% of memory is used if all process slots are occupied.
        /// </remarks>
        internal int DefaultMemoryUsageMbPerProcess => (int)((TotalRamMb ?? 0) * 0.8 / Math.Max(TotalProcessSlots, Environment.ProcessorCount));

        internal int DefaultCommitUsageMbPerProcess => (int)(DefaultMemoryUsageMbPerProcess * 1.5);

        /// <summary>
        /// Listen for status change events on the worker
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1009:DeclareEventHandlersCorrectly")]
        [SuppressMessage("Microsoft.Naming", "CA1710:IdentifiersShouldHaveCorrectSuffix")]
        public event Action<Worker> StatusChanged;

        /// <summary>
        /// Listen for status change events on the worker
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1009:DeclareEventHandlersCorrectly")]
        [SuppressMessage("Microsoft.Naming", "CA1710:IdentifiersShouldHaveCorrectSuffix")]
        internal event WorkerResourceChangedHandler ResourcesChanged;

        /// <summary>
        /// The status of the worker node
        /// </summary>
        public virtual WorkerNodeStatus Status
        {
            get
            {
                return m_status;
            }

            set
            {
                m_status = value;
                OnStatusChanged();
            }
        }

        /// <summary>
        /// Whether the worker become available at any time
        /// </summary>
        public virtual bool EverAvailable
        {
            get
            {
                return true;
            }
        }

        /// <summary>
        /// The number of the build requests waiting to be sent
        /// </summary>
        public virtual int WaitingBuildRequestsCount
        {
            get
            {
                return 0;
            }
        }

        /// <summary>
        /// Gets the name of the worker
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Snapshot of pip state counts for worker
        /// </summary>
        public WorkerPipStateManager.Snapshot PipStateSnapshot { get; }

        private readonly OperationKind m_workerOperationKind;

        /// <summary>
        /// Constructor
        /// </summary>
        protected Worker(uint workerId, string name)
        {
            WorkerId = workerId;
            Name = name;
            m_workerSemaphores = new SemaphoreSet<StringId>();
            m_workerPipStateManager = new WorkerPipStateManager();
            PipStateSnapshot = m_workerPipStateManager.GetSnapshot();

            m_workerOperationKind = OperationKind.Create("Worker " + Name);
            DrainCompletion = TaskSourceSlim.Create<bool>();
        }

        /// <summary>
        /// Initializes the worker
        /// </summary>
        public virtual void Start()
        {
            Status = WorkerNodeStatus.Running;
        }

        /// <summary>
        /// Signals that build is finished and that worker should exit
        /// </summary>
#pragma warning disable 1998 // Disable the warning for "This async method lacks 'await'"
        public virtual async Task FinishAsync(string buildFailure, [CallerMemberName] string callerName = null)
        {
            Status = WorkerNodeStatus.Stopped;
        }
#pragma warning restore 1998

        /// <summary>
        /// Release worker before build is finished due to the insufficient amount of work left
        /// </summary>
#pragma warning disable 1998 // Disable the warning for "This async method lacks 'await'"
        public virtual async Task EarlyReleaseAsync()
        {
            throw new NotImplementedException("Local worker does not support early release");
        }
#pragma warning restore 1998

        /// <summary>
        /// Returns if true if the worker holds a local node; false otherwise.
        /// </summary>
        public bool IsLocal => WorkerId == LocalWorkerIndex;

        /// <summary>
        /// Returns if true if the worker holds a remote node; false otherwise.
        /// </summary>
        public bool IsRemote => !IsLocal;

        /// <summary>
        /// Whether the worker is available to acquire work items
        /// </summary>
        public bool IsAvailable => Status == WorkerNodeStatus.Running;

        /// <summary>
        /// Gets the currently acquired slots for all operations that can be done on a worker.
        /// </summary>
        public int AcquiredSlots => AcquiredProcessSlots + AcquiredCacheLookupSlots + AcquiredIpcSlots;

        /// <summary>
        /// Gets the currently acquired slots for process pips.
        /// </summary>
        public int AcquiredSlotsForProcessPips => AcquiredProcessSlots + AcquiredCacheLookupSlots;

        /// <summary>
        /// Gets the currently acquired process slots
        /// </summary>
        public int AcquiredProcessSlots => Volatile.Read(ref m_acquiredProcessSlots);

        /// <summary>
        /// Gets the currently acquired cache lookup slots
        /// </summary>
        public int AcquiredCacheLookupSlots => Volatile.Read(ref m_acquiredCacheLookupSlots);

        /// <summary>
        /// Gets the currently acquired IPC slots.
        /// </summary>
        public int AcquiredIpcSlots => Volatile.Read(ref m_acquiredIpcSlots);

        /// <summary>
        /// Whether the content tracking is enabled.
        /// </summary>
        /// <remarks>
        /// Content tracking is enabled when there are remote workers in the scheduler.
        /// </remarks>
        public bool IsContentTrackingEnabled => m_availableContent != null;

        /// <summary>
        /// Ensures that this worker instance has the same resource mappings as the given worker
        /// </summary>
        internal void SyncResourceMappings(Worker worker)
        {
            m_workerSemaphores = worker.m_workerSemaphores.CreateSharingCopy();
        }

        internal void TrackStatusOperation(OperationContext parent)
        {
            m_workerStatusOperation = parent.StartAsyncOperation(m_workerOperationKind);
        }

        internal void UpdateStatusOperation()
        {
            if (m_workerStatusOperation.IsValid)
            {
                var status = Status;
                m_currentStatusOperation.Dispose();
                if (status != WorkerNodeStatus.Stopped)
                {
                    m_currentStatusOperation = m_workerStatusOperation.StartAsyncOperation(WorkerStatusOperationKinds[(int)status]);
                }
            }
        }

        /// <summary>
        /// Raises <see cref="StatusChanged"/> event.
        /// </summary>
        protected void OnStatusChanged()
        {
            StatusChanged?.Invoke(this);
            OnWorkerResourcesChanged(WorkerResource.Status, increased: Status == WorkerNodeStatus.Running);
        }

        /// <summary>
        /// Raises <see cref="ResourcesChanged"/> event.
        /// </summary>
        private void OnWorkerResourcesChanged(WorkerResource kind, bool increased)
        {
            ResourcesChanged?.Invoke(this, kind, increased);
        }

        /// <summary>
        /// Attempts to acquire a cache lookup slot on the worker
        /// </summary>
        /// <param name="runnablePip">the pip</param>
        /// <param name="force">true to force acquisition of the slot</param>
        /// <returns>true if the slot was acquired. False, otherwise.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters")]
        public bool TryAcquireCacheLookup(ProcessRunnablePip runnablePip, bool force)
        {
            using (EarlyReleaseLock.AcquireReadLock())
            {
                if (!IsAvailable)
                {
                    return false;
                }

                if (force)
                {
                    Interlocked.Increment(ref m_acquiredCacheLookupSlots);
                    runnablePip.AcquiredResourceWorker = this;
                    return true;
                }

                // Atomically acquire a cache lookup slot, being sure to not increase above the limit
                while (true)
                {
                    int acquiredCacheLookupSlots = AcquiredCacheLookupSlots;
                    if (acquiredCacheLookupSlots < TotalCacheLookupSlots)
                    {
                        if (Interlocked.CompareExchange(ref m_acquiredCacheLookupSlots, acquiredCacheLookupSlots + 1, acquiredCacheLookupSlots) == acquiredCacheLookupSlots)
                        {
                            OnWorkerResourcesChanged(WorkerResource.AvailableCacheLookupSlots, increased: false);
                            runnablePip.AcquiredResourceWorker = this;
                            return true;
                        }
                        else
                        {
                            // Failed to update value. Retry.
                        }
                    }
                    else
                    {
                        return false;
                    }
                }
            }
        }

        /// <summary>
        /// Try acquire given resources on the worker. This must be called from a thread-safe context to prevent race conditions.
        /// </summary>
        internal bool TryAcquire(RunnablePip runnablePip, out WorkerResource? limitingResource, double loadFactor = 1)
        {
            Contract.Requires(runnablePip.PipType == PipType.Ipc || runnablePip.PipType == PipType.Process);
            Contract.Ensures(Contract.Result<bool>() == (limitingResource == null), "Must set a limiting resource when resources cannot be acquired");
            using (EarlyReleaseLock.AcquireReadLock())
            {
                if (!IsAvailable)
                {
                    limitingResource = WorkerResource.Status;
                    return false;
                }

                if (runnablePip.PipType == PipType.Ipc)
                {
                    Interlocked.Increment(ref m_acquiredIpcSlots);
                    runnablePip.AcquiredResourceWorker = this;
                    limitingResource = null;
                    return true;
                }

                if (IsLocal)
                {
                    // Local worker does not use load factor as it may be down throttle by the
                    // scheduler in order to handle remote requests.
                    loadFactor = 1;
                }

                var processRunnablePip = runnablePip as ProcessRunnablePip;
                // If a process has a weight higher than the total number of process slots, still allow it to run as long as there are no other
                // processes running (the number of acquired slots is 0)
                if (AcquiredProcessSlots != 0 && AcquiredProcessSlots + processRunnablePip.Weight > (EffectiveTotalProcessSlots * loadFactor))
                {
                    limitingResource = WorkerResource.AvailableProcessSlots;
                    return false;
                }

                StringId limitingResourceName = StringId.Invalid;
                if (processRunnablePip.TryAcquireResources(m_workerSemaphores, GetAdditionalResourceInfo(processRunnablePip), out limitingResourceName))
                {
                    Interlocked.Add(ref m_acquiredProcessSlots, processRunnablePip.Weight);
                    OnWorkerResourcesChanged(WorkerResource.AvailableProcessSlots, increased: false);
                    runnablePip.AcquiredResourceWorker = this;
                    limitingResource = null;
                    return true;
                }

                limitingResource = limitingResourceName == m_ramSemaphoreNameId
                    ? WorkerResource.AvailableMemoryMb
                    : WorkerResource.CreateSemaphoreResource(limitingResourceName.ToString(runnablePip.Environment.Context.StringTable));
                return false;
            }
        }

        private ProcessSemaphoreInfo[] GetAdditionalResourceInfo(ProcessRunnablePip runnableProcess)
        {
            if (TotalRamMb == null || TotalCommitMb == null || runnableProcess.Environment.Configuration.Schedule.UseHistoricalRamUsageInfo != true)
            {
                // Not tracking working set or commit memory
                return null;
            }

            if (!m_ramSemaphoreNameId.IsValid)
            {
                m_ramSemaphoreNameId = runnableProcess.Environment.Context.StringTable.AddString(RamSemaphoreName);
                m_ramSemaphoreIndex = m_workerSemaphores.CreateSemaphore(m_ramSemaphoreNameId, ProcessExtensions.PercentageResourceLimit);
            }

            if (!m_commitSemaphoreNameId.IsValid)
            {
                m_commitSemaphoreNameId = runnableProcess.Environment.Context.StringTable.AddString(CommitSemaphoreName);
                m_commitSemaphoreIndex = m_workerSemaphores.CreateSemaphore(m_commitSemaphoreNameId, ProcessExtensions.PercentageResourceLimit);
            }

            var expectedMemoryCounters = GetExpectedMemoryCounters(runnableProcess);

            return new ProcessSemaphoreInfo[]
            {
                ProcessExtensions.GetNormalizedPercentageResource(
                    m_ramSemaphoreNameId,
                    usage: expectedMemoryCounters.PeakWorkingSetMb,
                    total: TotalRamMb.Value),
                ProcessExtensions.GetNormalizedPercentageResource(
                    m_commitSemaphoreNameId,
                    usage: expectedMemoryCounters.PeakCommitUsageMb,
                    total: TotalCommitMb.Value),
            };
        }

        /// <summary>
        /// Gets the estimated memory counters for the process
        /// </summary>
        public ProcessMemoryCounters GetExpectedMemoryCounters(ProcessRunnablePip runnableProcess)
        {
            if (TotalRamMb == null || TotalCommitMb == null)
            {
                return ProcessMemoryCounters.CreateFromMb(0, 0, 0);
            }

            if (runnableProcess.ExpectedMemoryCounters == null)
            {
                return ProcessMemoryCounters.CreateFromMb(
                    peakVirtualMemoryUsageMb: DefaultMemoryUsageMbPerProcess,
                    peakWorkingSetMb: DefaultMemoryUsageMbPerProcess,
                    peakCommitUsageMb: DefaultCommitUsageMbPerProcess);
            }

            var expectedMemoryCounters = runnableProcess.ExpectedMemoryCounters.Value;

            // 5% more to give some slack
            return ProcessMemoryCounters.CreateFromMb(
                peakVirtualMemoryUsageMb: (int) (expectedMemoryCounters.PeakVirtualMemoryUsageMb * 1.05),
                peakWorkingSetMb: (int)(expectedMemoryCounters.PeakWorkingSetMb * 1.05),
                peakCommitUsageMb: (int)(expectedMemoryCounters.PeakCommitUsageMb * 1.05));
        }

        /// <summary>
        /// Release pip's resources after worker is done with the task
        /// </summary>
        public void ReleaseResources(RunnablePip runnablePip)
        {
            Contract.Assert(runnablePip.AcquiredResourceWorker == this);

            runnablePip.AcquiredResourceWorker = null;

            var processRunnablePip = runnablePip as ProcessRunnablePip;
            if (processRunnablePip != null)
            {
                if (runnablePip.Step == PipExecutionStep.CacheLookup)
                {
                    Interlocked.Decrement(ref m_acquiredCacheLookupSlots);
                    OnWorkerResourcesChanged(WorkerResource.AvailableCacheLookupSlots, increased: true);
                    runnablePip.SetWorker(null);
                }
                else
                {
                    Contract.Assert(processRunnablePip.Resources.HasValue);

                    Interlocked.Add(ref m_acquiredProcessSlots, -processRunnablePip.Weight);

                    var resources = processRunnablePip.Resources.Value;
                    m_workerSemaphores.ReleaseResources(resources);

                    OnWorkerResourcesChanged(WorkerResource.AvailableProcessSlots, increased: true);
                }
            }

            if (runnablePip.PipType == PipType.Ipc)
            {
                Interlocked.Decrement(ref m_acquiredIpcSlots);
            }

            if (AcquiredSlots == 0 && Status == WorkerNodeStatus.Stopping)
            {
                DrainCompletion.TrySetResult(true);
            }
        }

        /// <summary>
        /// Adjusts the total process slots
        /// </summary>
        public void AdjustTotalProcessSlots(int newTotalSlots)
        {
            TotalProcessSlots = newTotalSlots;
        }

        /// <summary>
        /// Adjusts the total cache lookup slots
        /// </summary>
        public void AdjustTotalCacheLookupSlots(int newTotalSlots)
        {
            TotalCacheLookupSlots = newTotalSlots;
        }

        #region Pip Operations

        /// <summary>
        /// Materializes the inputs of the pip
        /// </summary>
        public virtual Task<PipResultStatus> MaterializeInputsAsync(RunnablePip runnablePip)
        {
            Contract.Requires(runnablePip.Step == PipExecutionStep.MaterializeInputs);
            throw Contract.AssertFailure(I($"MaterializeInputsAsync is not supported for worker {Name}"));
        }

        /// <summary>
        /// Materializes the outputs of the pip
        /// </summary>
        public virtual Task<PipResultStatus> MaterializeOutputsAsync(RunnablePip runnablePip)
        {
            Contract.Requires(runnablePip.Step == PipExecutionStep.MaterializeOutputs);
            throw Contract.AssertFailure(I($"MaterializeOutputsAsync is not supported for worker {Name}"));
        }

        /// <summary>
        /// Executes a process pip
        /// </summary>
        public virtual Task<ExecutionResult> ExecuteProcessAsync(ProcessRunnablePip runnablePip)
        {
            Contract.Requires(runnablePip.Step == PipExecutionStep.ExecuteProcess);
            throw Contract.AssertFailure(I($"ExecuteProcessAsync is not supported for worker {Name}"));
        }

        /// <summary>
        /// Executes an IPC pip
        /// </summary>
        public virtual Task<PipResult> ExecuteIpcAsync(RunnablePip runnablePip)
        {
            Contract.Requires(runnablePip.PipType == PipType.Ipc);
            Contract.Requires(runnablePip.Step == PipExecutionStep.ExecuteNonProcessPip);
            throw Contract.AssertFailure(I($"ExecuteIpcAsync is not supported for worker {Name}"));
        }

        /// <summary>
        /// Executes PostProcess on the worker
        /// </summary>
        public virtual Task<ExecutionResult> PostProcessAsync(ProcessRunnablePip runnablePip)
        {
            Contract.Requires(runnablePip.Step == PipExecutionStep.PostProcess);
            throw Contract.AssertFailure(I($"{nameof(PostProcessAsync)} is not supported for worker {Name}"));
        }

        /// <summary>
        /// Performs a cache lookup for the process on the worker
        /// </summary>
        public virtual Task<RunnableFromCacheResult> CacheLookupAsync(
            ProcessRunnablePip runnablePip,
            PipExecutionState.PipScopeState state,
            CacheableProcess cacheableProcess)
        {
            Contract.Requires(runnablePip.Step == PipExecutionStep.CacheLookup);
            throw Contract.AssertFailure(I($"CacheLookupAsync is not supported for worker {Name}"));
        }

        #endregion

        /// <inheritdoc/>
        public virtual void Dispose()
        {
        }

        #region Content Tracking

        /// <summary>
        /// Initializes the worker after attach
        /// </summary>
        public virtual void Initialize(PipGraph pipGraph, IExecutionLogTarget executionLogTarget)
        {
            m_availableContent = new ContentTrackingSet(pipGraph);
            m_availableHashes = new ContentTrackingSet(pipGraph);
        }

        /// <summary>
        /// In case of a failed build request call after many retries, we reset available hashes 
        /// to make sure that we do not overestimate what the worker contains.
        /// </summary>
        protected void ResetAvailableHashes(PipGraph pipGraph)
        {
            m_availableHashes = new ContentTrackingSet(pipGraph);
        }

        /// <summary>
        /// Called before worker starts executing the IPC or process pip
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope")]
        protected PipExecutionScope OnPipExecutionStarted(RunnablePip runnable, OperationContext operationContext = default(OperationContext))
        {
            operationContext = operationContext.IsValid ? operationContext : runnable.OperationContext;
            var scope = new PipExecutionScope(runnable, this, operationContext);

            if (IsContentTrackingEnabled)
            {
                // Only perform this operation for distributed master.
                var pip = runnable.Pip;
                var description = runnable.Description;
                Logger.Log.DistributionExecutePipRequest(operationContext, pip.SemiStableHash, description, Name, runnable.Step.AsString());
            }

            return scope;
        }

        /// <summary>
        /// Called after worker finishes executing the IPC or process pip
        /// </summary>
        private void OnPipExecutionCompletion(RunnablePip runnable)
        {
            if (!IsContentTrackingEnabled)
            {
                // Only perform this operation for distributed master.
                return;
            }

            var operationContext = runnable.OperationContext;
            var pip = runnable.Pip;
            var description = runnable.Description;
            var executionResult = runnable.ExecutionResult;

            Logger.Log.DistributionFinishedPipRequest(operationContext, pip.SemiStableHash, description, Name, runnable.Step.AsString());

            if (executionResult == null)
            {
                return;
            }

            if ((runnable.Step == PipExecutionStep.PostProcess && !executionResult.Converged) ||
                (!executionResult.Result.IndicatesFailure() && runnable.Step == PipExecutionStep.ExecuteNonProcessPip))
            {
                // After post process, if process was not converged (i.e. process execution outputs are used
                // as results because there was no conflicting cache entry when storing to cache),
                // report that the worker has the output content
                // IPC pips don't use cache convergence so always report their outputs
                foreach (var outputContent in executionResult.OutputContent)
                {
                    TryAddAvailableContent(outputContent.fileArtifact);
                }

                foreach (var directoryContent in executionResult.DirectoryOutputs)
                {
                    TryAddAvailableContent(directoryContent.directoryArtifact);
                }
            }

            if (IsRemote &&
                (runnable.Step == PipExecutionStep.ExecuteProcess || runnable.Step == PipExecutionStep.ExecuteNonProcessPip))
            {
                // Log the outputs reported from the worker for the pip execution
                foreach (var outputFile in executionResult.OutputContent)
                {
                    // NOTE: Available content is not added to the content tracking set here as the content
                    // may be changed due to cache convergence
                    Logger.Log.DistributionMasterWorkerProcessOutputContent(
                        operationContext,
                        pip.SemiStableHash,
                        description,
                        outputFile.fileArtifact.Path.ToString(runnable.Environment.Context.PathTable),
                        outputFile.fileInfo.Hash.ToHex(),
                        outputFile.fileInfo.ReparsePointInfo.ToString(),
                        Name);
                }
            }
        }

        /// <summary>
        /// Transitions the state of the given pip on the worker
        /// </summary>
        public virtual void Transition(PipId pipId, WorkerPipState state)
        {
            m_workerPipStateManager.Transition(pipId, state);
        }

        /// <summary>
        /// Called after worker finishes materializing inputs for a pip
        /// </summary>
        public void OnInputMaterializationCompletion(Pip pip, IPipExecutionEnvironment environment)
        {
            Contract.Assert(pip.PipType == PipType.Process || pip.PipType == PipType.Ipc);

            var fileContentManager = environment.State.FileContentManager;

            // Update the pip state
            Transition(pip.PipId, WorkerPipState.Prepped);

            if (!IsContentTrackingEnabled)
            {
                return;
            }

            fileContentManager.CollectPipInputsToMaterialize(
                environment.PipTable,
                pip,
                files: null,
                filter: artifact =>
                {
                    if (artifact.IsFile && artifact.FileArtifact.IsSourceFile)
                    {
                        // Do not register the source files as the available content.
                        return false;
                    }

                    bool added = TryAddAvailableContent(artifact);
                    if (artifact.IsFile)
                    {
                        // Don't attempt to add anything. Just need to register the available content
                        return false;
                    }
                    else
                    {
                        // Process directories to visit files unless they were already added
                        return !added;
                    }
                },
                serviceFilter: servicePipId =>
                {
                    bool added = TryAddAvailableServiceInputContent(servicePipId);

                    // Don't attempt to add anything. Just need to register the available content
                    return false;
                });
        }

        /// <summary>
        /// Gets whether the file's hash sent to the worker
        /// </summary>
        public bool? TryAddAvailableHash(in FileOrDirectoryArtifact artifact)
        {
            return m_availableHashes.Add(artifact);
        }

        /// <summary>
        /// Gets whether the service pip id content hashes sent to the worker
        /// </summary>
        public bool? TryAddAvailableHash(PipId servicePipId)
        {
            return m_availableHashes.Add(servicePipId);
        }

        /// <summary>
        /// Adds the content to the available content for the worker
        /// </summary>
        public bool TryAddAvailableContent(in FileOrDirectoryArtifact artifact)
        {
            return m_availableContent.Add(artifact) ?? false;
        }

        /// <summary>
        /// Adds the service input to the available content for the worker
        /// </summary>
        public bool TryAddAvailableServiceInputContent(PipId servicePipId)
        {
            return m_availableContent.Add(servicePipId) ?? false;
        }

        /// <summary>
        /// Gets whether the content is materialized on the worker
        /// </summary>
        public bool HasContent(in FileOrDirectoryArtifact artifact)
        {
            return m_availableContent.Contains(artifact);
        }

        /// <summary>
        /// Gets whether the service input content is materialized on the worker
        /// </summary>
        public bool HasServiceInputContent(PipId servicePipId)
        {
            return m_availableContent.Contains(servicePipId);
        }

        #endregion

        /// <summary>
        /// Tracks the extent of a pip step execution on a worker
        /// </summary>
        protected sealed class PipExecutionScope : IDisposable
        {
            private readonly RunnablePip m_runnablePip;
            private readonly Worker m_worker;
            private readonly OperationContext m_operationContext;

            /// <nodoc />
            public PipExecutionScope(RunnablePip runnablePip, Worker worker, OperationContext operationContext)
            {
                m_runnablePip = runnablePip;
                m_worker = worker;
                m_operationContext = operationContext.StartOperation(worker.m_workerOperationKind);
            }

            /// <nodoc />
            public void Dispose()
            {
                m_worker.OnPipExecutionCompletion(m_runnablePip);
                m_operationContext.Dispose();
            }
        }
    }
}
