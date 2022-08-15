// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Health;
using System.Fabric.Query;
using System.Fabric.Repair;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Utilities.Telemetry;
using FabricHealer.Interfaces;
using Guan.Logic;
using FabricHealer.Repair.Guan;
using FabricHealer.Utilities;

namespace FabricHealer.Repair
{
    public class RepairTaskManager : IRepairTasks
    {
        private static readonly TimeSpan MaxWaitTimeForInfraRepairTaskCompleted = TimeSpan.FromHours(2);
        private readonly RepairTaskEngine repairTaskEngine;
        private readonly RepairExecutor RepairExec;
        private readonly TimeSpan AsyncTimeout = TimeSpan.FromSeconds(60);
        private readonly DateTime HealthEventsListCreationTime = DateTime.UtcNow;
        private readonly TimeSpan MaxLifeTimeHealthEventsData = TimeSpan.FromDays(2);
        private DateTime LastHealthEventsListClearDateTime;
        internal readonly List<HealthEvent> DetectedHealthEvents = new List<HealthEvent>();
        internal readonly StatelessServiceContext Context;
        internal readonly CancellationToken Token;
        internal readonly TelemetryUtilities TelemetryUtilities;

        public RepairTaskManager(StatelessServiceContext context, CancellationToken token)
        {
            Context = context;
            Token = token;
            RepairExec = new RepairExecutor(context, token);
            repairTaskEngine = new RepairTaskEngine();
            TelemetryUtilities = new TelemetryUtilities(context);
            LastHealthEventsListClearDateTime = HealthEventsListCreationTime;
        }

        // TODO.
        public Task<bool> RemoveServiceFabricNodeStateAsync(string nodeName, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        public async Task ActivateServiceFabricNodeAsync(string nodeName, CancellationToken cancellationToken)
        {
            await FabricHealerManager.FabricClientSingleton.ClusterManager.ActivateNodeAsync(nodeName, AsyncTimeout, cancellationToken);
        }

        public async Task<bool> SafeRestartServiceFabricNodeAsync(TelemetryData repairData, RepairTask repairTask, CancellationToken cancellationToken)
        {
            if (!await RepairExec.SafeRestartFabricNodeAsync(repairData, repairTask, cancellationToken))
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "SafeRestartFabricNodeAsync",
                        $"Did not restart Fabric node {repairData.NodeName}",
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "SafeRestartFabricNodeAsync",
                    $"Successfully restarted Fabric node {repairData.NodeName}",
                    cancellationToken,
                    repairData,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

            return true;
        }

        public async Task StartRepairWorkflowAsync(TelemetryData repairData, List<string> repairRules, CancellationToken cancellationToken)
        {
            Node node = null;

            if (repairData.NodeName != null)
            {
                node = await GetFabricNodeFromNodeNameAsync(repairData.NodeName, cancellationToken);
            }

            if (node == null)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "RepairTaskManager.StartRepairWorkflowAsync",
                        "Unable to locate target node. Aborting repair.",
                        cancellationToken,
                        null,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return;
            }

            if (string.IsNullOrEmpty(repairData.NodeType))
            {
                repairData.NodeType = node.NodeType;
            }

            try
            {
                await RunGuanQueryAsync(repairData, repairRules);
            }
            catch (GuanException ge)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "StartRepairWorkflowAsync:GuanException",
                        $"Failed in Guan: {ge}",
                        cancellationToken,
                        null,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);
            }
        }

        /// <summary>
        /// This is the entry point to Guan parsing and query execution. It creates the necessary Guan objects to successfully execute logic rules based on supplied FO data 
        /// and related repair rules.
        /// </summary>
        /// <param name="repairData">Health data from FO for target SF entity</param>
        /// <param name="repairRules">Repair rules that are related to target SF entity</param>
        /// <param name="repairExecutorData">Optional Repair data that is used primarily when some repair is being restarted (after an FH restart, for example)</param>
        /// <returns></returns>
        public async Task RunGuanQueryAsync(TelemetryData repairData, List<string> repairRules, RepairExecutorData repairExecutorData = null)
        {
            // Add predicate types to functor table. Note that all health information data from FO are automatically passed to all predicates.
            FunctorTable functorTable = new FunctorTable();

            // Add external helper predicates.
            functorTable.Add(CheckFolderSizePredicateType.Singleton(RepairConstants.CheckFolderSize, this, repairData));
            functorTable.Add(GetRepairHistoryPredicateType.Singleton(RepairConstants.GetRepairHistory, this, repairData));
            functorTable.Add(GetHealthEventHistoryPredicateType.Singleton(RepairConstants.GetHealthEventHistory, this, repairData));
            functorTable.Add(CheckInsideRunIntervalPredicateType.Singleton(RepairConstants.CheckInsideRunInterval, this, repairData));
            functorTable.Add(EmitMessagePredicateType.Singleton(RepairConstants.EmitMessage, this));

            // Add external repair predicates.
            functorTable.Add(DeleteFilesPredicateType.Singleton(RepairConstants.DeleteFiles, this, repairData));
            functorTable.Add(RestartCodePackagePredicateType.Singleton(RepairConstants.RestartCodePackage, this, repairData));
            functorTable.Add(RestartFabricNodePredicateType.Singleton(RepairConstants.RestartFabricNode, this, repairExecutorData, repairTaskEngine, repairData));
            functorTable.Add(RestartFabricSystemProcessPredicateType.Singleton(RepairConstants.RestartFabricSystemProcess, this, repairData));
            functorTable.Add(RestartReplicaPredicateType.Singleton(RepairConstants.RestartReplica, this, repairData));
            functorTable.Add(RestartMachinePredicateType.Singleton(RepairConstants.RestartVM, this, repairData));

            // Parse rules.
            Module module = Module.Parse("external", repairRules, functorTable);

            // Create guan query.
            var queryDispatcher = new GuanQueryDispatcher(module);

            /* Bind default arguments to goal (Mitigate). */

            List<CompoundTerm> compoundTerms = new List<CompoundTerm>();

            // Mitigate is the head of the rules used in FH. It's the goal that Guan will try to accomplish based on the logical expressions (or subgoals) that form a given rule.
            CompoundTerm compoundTerm = new CompoundTerm("Mitigate");

            // The type of metric that led FO to generate the unhealthy evaluation for the entity (App, Node, VM, Replica, etc).
            // We rename these for brevity for simplified use in logic rule composition (e;g., MetricName="Threads" instead of MetricName="Total Thread Count").
            repairData.Metric = SupportedErrorCodes.GetMetricNameFromErrorCode(repairData.Code);

            // These args hold the related values supplied by FO and are available anywhere Mitigate is used as a rule head.
            // Think of these as facts from FabricObserver.
            compoundTerm.AddArgument(new Constant(repairData.ApplicationName), RepairConstants.AppName);
            compoundTerm.AddArgument(new Constant(repairData.Code), RepairConstants.ErrorCode);
            compoundTerm.AddArgument(new Constant(Enum.GetName(typeof(HealthState), repairData.HealthState)), RepairConstants.HealthState);
            compoundTerm.AddArgument(new Constant(repairData.Metric), RepairConstants.MetricName);
            compoundTerm.AddArgument(new Constant(Convert.ToInt64(repairData.Value)), RepairConstants.MetricValue);
            compoundTerm.AddArgument(new Constant(repairData.NodeName), RepairConstants.NodeName);
            compoundTerm.AddArgument(new Constant(repairData.NodeType), RepairConstants.NodeType);
            compoundTerm.AddArgument(new Constant(repairData.ObserverName), RepairConstants.ObserverName);
            compoundTerm.AddArgument(new Constant(repairData.OS), RepairConstants.OS);
            compoundTerm.AddArgument(new Constant(Enum.GetName(typeof(ServiceKind), repairData.ServiceKind)), RepairConstants.ServiceKind);
            compoundTerm.AddArgument(new Constant(repairData.ServiceName), RepairConstants.ServiceName);
            compoundTerm.AddArgument(new Constant(repairData.ProcessId), RepairConstants.ProcessId);
            compoundTerm.AddArgument(new Constant(repairData.ProcessName), RepairConstants.ProcessName);
            compoundTerm.AddArgument(new Constant(repairData.ProcessStartTime), RepairConstants.ProcessStartTime);
            compoundTerm.AddArgument(new Constant(repairData.PartitionId), RepairConstants.PartitionId);
            compoundTerm.AddArgument(new Constant(repairData.ReplicaId), RepairConstants.ReplicaOrInstanceId);
            compoundTerms.Add(compoundTerm);

            // Run Guan query.
            // This is where the supplied rules are run with FO data that may or may not lead to mitigation of some supported SF entity in trouble (or a VM/Disk).
            await queryDispatcher.RunQueryAsync(compoundTerms);
        }

        // The repair will be executed by SF Infrastructure service, not FH. This is the case for all
        // VM-level repairs. IS will communicate with VMSS (for example) to guarantee safe repairs in MR-enabled
        // clusters.RM, as usual, will orchestrate the repair cycle.
        public async Task<bool> ExecuteRMInfrastructureRepairTask(TelemetryData repairData, CancellationToken cancellationToken)
        {
            var infraServices = await FabricRepairTasks.GetInfrastructureServiceInstancesAsync(cancellationToken);
            var arrServices = infraServices as Service[] ?? infraServices.ToArray();

            if (arrServices.Length == 0)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "ExecuteRMInfrastructureRepairTask",
                        "Infrastructure Service not found. Will not attemp VM repair.",
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }

            string executorName = null;

            foreach (var service in arrServices)
            {
                if (!service.ServiceName.OriginalString.Contains(repairData.NodeType))
                {
                    continue;
                }

                executorName = service.ServiceName.OriginalString;

                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairTaskManager.ExecuteRMInfrastructureRepairTask",
                        $"IS RepairTask {RepairTaskEngine.HostVMReboot} " +
                        $"Executor set to {executorName}.",
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                break;
            }

            if (executorName == null)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "ExecuteRMInfrastructureRepairTask",
                        "Unable to find InfrastructureService service instance." +
                        "Exiting RepairTaskManager.ScheduleFHRepairTaskAsync.",
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }

            // Make sure there is not already a repair job executing reboot repair for target node.
            var isRepairAlreadyInProgress =
                    await repairTaskEngine.IsFHRepairTaskRunningAsync(
                                             executorName,
                                             repairData,
                                             cancellationToken);

            if (isRepairAlreadyInProgress)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "ExecuteRMInfrastructureRepairTask",
                        "Virtual machine repair task for VM " +
                        $"{await RepairExec.GetMachineHostNameFromFabricNodeNameAsync(repairData.NodeName, cancellationToken)} " +
                        "is already in progress. Will not schedule another VM repair at this time.",
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }

            // Create repair task for target node.
            var repairTask = await FabricRepairTasks.ScheduleRepairTaskAsync(repairData, null, executorName, cancellationToken);

            if (repairTask == null)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "ExecuteRMInfrastructureRepairTask",
                        "Unable to create Repair Task.",
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "ExecuteRMInfrastructureRepairTask",
                    $"Successfully created Repair Task {repairTask.TaskId}",
                    cancellationToken,
                    repairData,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

            var timer = Stopwatch.StartNew();

            // It can take a while to get from a VM reboot/reimage to a healthy Fabric node, so block here until repair completes.
            // Note that, by design, this will block any other FabricHealer-initiated repair from taking place in the cluster.
            // FabricHealer is designed to be very conservative with respect to node level repairs. 
            // It is a good idea to not change this default behavior.
            while (timer.Elapsed < MaxWaitTimeForInfraRepairTaskCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!await FabricRepairTasks.IsRepairTaskInDesiredStateAsync(
                            repairTask.TaskId,
                            executorName,
                            new List<RepairTaskState> { RepairTaskState.Completed }))
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                    continue;
                }

                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "ExecuteRMInfrastructureRepairTask::Completed",
                        $"Successfully completed repair {repairData.RepairPolicy.RepairId}",
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                timer.Stop();
                return true;
            }

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "ExecuteRMInfrastructureRepairTask::Timeout",
                    $"Max wait time of {MaxWaitTimeForInfraRepairTaskCompleted} has elapsed for repair " +
                    $"{repairData.RepairPolicy.RepairId}.",
                    cancellationToken,
                    repairData,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

            return false;
        }

        public async Task<bool> DeleteFilesAsyncAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            return await RepairExec.DeleteFilesAsync(
                            repairData ?? throw new ArgumentException(nameof(repairData)),
                            cancellationToken);
        }

        public async Task<bool> RestartReplicaAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            return await RepairExec.RestartReplicaAsync(
                            repairData ?? throw new ArgumentException("repairData can't be null."),
                            cancellationToken);
        }

        public async Task<bool> RemoveReplicaAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            return await RepairExec.RemoveReplicaAsync(
                            repairData ?? throw new ArgumentException("repairData can't be null."),
                            cancellationToken);
        }

        public async Task<bool> RestartDeployedCodePackageAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            var result = await RepairExec.RestartDeployedCodePackageAsync(
                                  repairData ?? throw new ArgumentException("repairData can't be null."),
                                  cancellationToken);

            return result != null;
        }

        /// <summary>
        /// Restarts Service Fabric system service process.
        /// </summary>
        /// <param name="repairData">repairData instance.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A Task containing a boolean value representing success or failure of the repair action.</returns>
        private async Task<bool> RestartSystemServiceProcessAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(repairData.ProcessName))
            {
                return false;
            }

            // Can only kill processes on the same node where FH instance that took the job is running.
            if (repairData.NodeName != Context.NodeContext.NodeName)
            {
                return false;
            }

            string actionMessage =
               $"Attempting to restart Service Fabric system process {repairData.ProcessName}.";

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.RestartSystemServiceProcessAsync::Start",
                    actionMessage,
                    cancellationToken,
                    repairData,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

            bool result = await RepairExec.RestartSystemServiceProcessAsync(repairData, cancellationToken);

            if (!result)
            {
                return false;
            }

            string statusSuccess = $"Successfully restarted Service Fabric system service process {repairData.ProcessName} on node {repairData.NodeName}.";

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.RestartSystemServiceProcessAsync::Success",
                    statusSuccess,
                    cancellationToken,
                    repairData,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

            return true;
        }

        private async Task<Node> GetFabricNodeFromNodeNameAsync(string nodeName, CancellationToken cancellationToken)
        {
            try
            {
                var nodes = await FabricHealerManager.FabricClientSingleton.QueryManager.GetNodeListAsync(nodeName, AsyncTimeout, cancellationToken);
                return nodes.Count > 0 ? nodes[0] : null;
            }
            catch (FabricException fe)
            {
                FabricHealerManager.RepairLogger.LogError($"Error getting node {nodeName}:{Environment.NewLine}{fe}");
                return null;
            }
        }

        public async Task<RepairTask> ScheduleFabricHealerRepairTaskAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            await Task.Delay(new Random().Next(100, 1500));

            // Has the repair already been scheduled by a different FH instance?
            if (await repairTaskEngine.IsFHRepairTaskRunningAsync(RepairTaskEngine.FHTaskIdPrefix, repairData, cancellationToken))
            {
                return null;
            }

            // Don't attempt a node-level repair on a node where there is already an active node-level repair.
            var currentlyExecutingRepairs =
                await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                        RepairTaskEngine.FHTaskIdPrefix,
                        RepairTaskStateFilter.Active | RepairTaskStateFilter.Approved | RepairTaskStateFilter.Executing,
                        RepairTaskEngine.FabricHealerExecutorName,
                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                        cancellationToken);

            if (currentlyExecutingRepairs.Count > 0)
            {
                foreach (var repair in currentlyExecutingRepairs.Where(task => task.ExecutorData.Contains(repairData.NodeName)))
                {
                    if (!JsonSerializationUtility.TryDeserialize(repair.ExecutorData, out RepairExecutorData repairExecutorData))
                    {
                        continue;
                    }

                    if (repairExecutorData.RepairData.RepairPolicy.RepairAction != RepairActionType.RestartFabricNode &&
                        repairExecutorData.RepairData.RepairPolicy.RepairAction != RepairActionType.RestartVM)
                    {
                        continue;
                    }

                    string message =
                        $"Node {repairData.NodeName} already has a node-impactful repair in progress: " +
                        $"{Enum.GetName(typeof(RepairActionType), repairData.RepairPolicy.RepairAction)}: {repair.TaskId}" +
                        "Exiting RepairTaskManager.ScheduleFabricHealerRmRepairTaskAsync.";

                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "ScheduleRepairTask::NodeRepairAlreadyInProgress",
                            message,
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return null;
                }
            }

            var executorData = new RepairExecutorData
            {
                ExecutorTimeoutInMinutes = (int)MaxWaitTimeForInfraRepairTaskCompleted.TotalMinutes, 
                RepairData = repairData
            };

            // Create custom FH repair task for target node.
            var repairTask = await FabricRepairTasks.ScheduleRepairTaskAsync(
                                    repairData,
                                    executorData,
                                    RepairTaskEngine.FabricHealerExecutorName,
                                    cancellationToken);
            return repairTask;
        }

        public async Task<bool> ExecuteFabricHealerRmRepairTaskAsync(RepairTask repairTask, TelemetryData repairData, CancellationToken cancellationToken)
        {
            if (repairTask == null)
            {
                return false;
            }

            TimeSpan approvalTimeout = TimeSpan.FromMinutes(10);
            Stopwatch stopWatch = Stopwatch.StartNew();
            bool isApproved = false;

            var repairs = await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, cancellationToken);

            if (repairs.All(repair => repair.TaskId != repairTask.TaskId))
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                        $"Failed to find scheduled repair task {repairTask.TaskId}.",
                        Token,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairTaskManager::WaitingForApproval",
                    $"Waiting for RM to Approve repair task {repairTask.TaskId}.",
                    cancellationToken,
                    repairData,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

            while (approvalTimeout >= stopWatch.Elapsed)
            {
                repairs = await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, cancellationToken);

                // Was repair cancelled (or cancellation requested) by another FH instance for some reason? Could be due to FH going down or a new deployment or a bug (fix it...).
                if (repairs.Any(repair => repair.TaskId == repairTask.TaskId
                                       && (repair.State == RepairTaskState.Completed && repair.ResultStatus == RepairTaskResult.Cancelled
                                           || repair.Flags == RepairTaskFlags.CancelRequested || repair.Flags == RepairTaskFlags.AbortRequested)))
                {
                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                            $"Repair Task {repairTask.TaskId} was aborted or cancelled.",
                            Token,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return false;
                }

                if (!repairs.Any(repair => repair.TaskId == repairTask.TaskId && repair.State == RepairTaskState.Approved))
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    continue;
                }

                isApproved = true;
                break;
            }

            stopWatch.Stop();
            stopWatch.Reset();

            if (isApproved)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync_Approved",
                        $"RM has Approved repair task {repairTask.TaskId}.",
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);
            }
            else
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync_NotApproved",
                        $"RM did not Approve repair task {repairTask.TaskId}. Cancelling...",
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                await FabricRepairTasks.CancelRepairTaskAsync(repairTask);
                return false;
            }

            _ = await FabricRepairTasks.SetFabricRepairJobStateAsync(
                        repairTask,
                        RepairTaskState.Executing,
                        RepairTaskResult.Pending,
                        cancellationToken);

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync_MovedExecuting",
                    $"Executing repair {repairTask.TaskId}.",
                    cancellationToken,
                    repairData,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

            bool success;
            var repairAction = repairData.RepairPolicy.RepairAction;

            try
            {
                switch (repairAction)
                {
                    case RepairActionType.DeleteFiles:
                            
                        success = await DeleteFilesAsyncAsync(repairData, cancellationToken);
                        break;

                    // Note: For SF app container services, RestartDeployedCodePackage API does not work.
                    // Thus, using Restart/Remove(stateful/stateless)Replica API instead, which does restart container instances.
                    case RepairActionType.RestartCodePackage:
                    {
                        if (string.IsNullOrWhiteSpace(repairData.ContainerId))
                        {
                            success = await RestartDeployedCodePackageAsync(repairData, cancellationToken);
                        }
                        else
                        {
                            if (repairData.PartitionId == null)
                            {
                                success = false;
                                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync::NoPartition",
                                        $"No partition specified.",
                                        cancellationToken,
                                        repairData,
                                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                                break;
                            }

                            // Need replica or instance details..
                            var repList = await FabricHealerManager.FabricClientSingleton.QueryManager.GetReplicaListAsync(
                                                (Guid)repairData.PartitionId,
                                                repairData.ReplicaId,
                                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                cancellationToken);

                            if (repList.Count == 0)
                            {
                                success = false;
                                break;
                            }

                            var rep = repList[0];

                            // Restarting stateful replica will restart the container instance.
                            if (rep.ServiceKind == ServiceKind.Stateful)
                            {
                                success = await RestartReplicaAsync(repairData, cancellationToken);
                            }
                            else
                            {
                                // For stateless intances, you need to remove the replica, which will
                                // restart the container instance.
                                success = await RemoveReplicaAsync(repairData, cancellationToken);
                            }
                        }

                        break;
                    }
                    case RepairActionType.RemoveReplica:
                    {
                        if (repairData.PartitionId == null)
                        {
                            success = false;
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync::NoPartition",
                                    $"No partition specified.",
                                    cancellationToken,
                                    repairData,
                                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                            break;
                        }

                        var repList = await FabricHealerManager.FabricClientSingleton.QueryManager.GetReplicaListAsync(
                                                (Guid)repairData.PartitionId,
                                                repairData.ReplicaId,
                                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                cancellationToken);

                        if (repList.Count == 0)
                        {
                            success = false;
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                                    $"Stateless Instance {repairData.ReplicaId} not found on partition " +
                                    $"{repairData.PartitionId}.",
                                    cancellationToken,
                                    repairData,
                                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                            break;
                        }

                        success = await RemoveReplicaAsync(repairData, cancellationToken);
                        break;
                    }
                    case RepairActionType.RestartProcess:
                    
                        success = await RestartSystemServiceProcessAsync(repairData, cancellationToken);
                        break;
                    
                    case RepairActionType.RestartReplica:
                    {
                        if (repairData.PartitionId == null)
                        {
                            success = false;
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync::NoPartition",
                                    $"No partition specified.",
                                    cancellationToken,
                                    repairData,
                                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                            break;
                        }

                        var repList = await FabricHealerManager.FabricClientSingleton.QueryManager.GetReplicaListAsync(
                                            (Guid)repairData.PartitionId,
                                            repairData.ReplicaId,
                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                            cancellationToken);

                        if (repList.Count == 0)
                        {
                            success = false;
                                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                                        $"Stateful replica {repairData.ReplicaId} not found on partition " +
                                        $"{repairData.PartitionId}.",
                                        cancellationToken,
                                        repairData,
                                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                                break;
                        }

                        var replica = repList[0];

                        var partition = await FabricHealerManager.FabricClientSingleton.QueryManager.GetPartitionAsync((Guid)repairData.PartitionId);
                        var partitionKind = partition[0].PartitionInformation.Kind;
                        // Restart - stateful replica.
                        if (replica.ServiceKind == ServiceKind.Stateful)
                        {
                            success = await RestartReplicaAsync(repairData, cancellationToken);
                        }
                        else
                        {
                            // For stateless replicas (aka instances), you need to remove the replica. The runtime will create a new one
                            // and place it.
                            success = await RemoveReplicaAsync(repairData, cancellationToken);
                        }

                        break;
                    }
                    case RepairActionType.RestartFabricNode:
                    {
                        var executorData = repairTask.ExecutorData;

                        if (string.IsNullOrWhiteSpace(executorData))
                        {

                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    "RepairTaskManager.SafeRestartFabricNode",
                                    $"Repair {repairTask.TaskId} is missing ExecutorData.",
                                    cancellationToken,
                                    repairData,
                                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                            success = false;
                        }
                        else
                        {
                            success = await SafeRestartServiceFabricNodeAsync(repairData, repairTask, cancellationToken);
                        }

                        break;
                    }
                    default:
                        return false;
                }
            }
            catch (FabricException)
            {
                return false;
            }

            // What was the target (a node, app, replica, etc..)?
            string repairTarget;

            switch (repairData.EntityType)
            {
                // FO..
                case EntityType.Application:
                    repairTarget = $"{repairData.ApplicationName} on node {repairData.NodeName}";

                    if (repairData.ApplicationName == RepairConstants.SystemAppName && !string.IsNullOrWhiteSpace(repairData.ProcessName))
                    {
                        repairTarget = $"{repairData.ProcessName} on node {repairData.NodeName}";
                    }
                    break;

                case EntityType.Disk:
                    repairTarget = $"{(repairData.RepairPolicy as DiskRepairPolicy)?.FolderPath} on machine hosting Fabric node {repairData.NodeName}";
                    break;

                case EntityType.Service:
                    repairTarget = $"{repairData.ServiceName} on node {repairData.NodeName}";
                    break;

                case EntityType.Process:
                    repairTarget = $"{repairData.ProcessName} on node {repairData.NodeName}";
                    break;

                case EntityType.Node:
                    repairTarget = $"{repairData.NodeName}";
                    break;

                case EntityType.Replica:
                    repairTarget = $"{repairData.ReplicaId}";
                    break;

                case EntityType.Partition:
                    repairTarget = $"{repairData.PartitionId}";
                    break;

                case EntityType.Machine:
                    repairTarget = $"Machine hosting Fabric node {repairData.NodeName}";
                    break;

                default:
                    throw new ArgumentException("Unknown repair target type.");
            }

            if (success)
            {
                string target = Enum.GetName(typeof(EntityType), repairData.EntityType);
                TimeSpan maxWaitForHealthStateOk = TimeSpan.FromMinutes(30);

                switch (repairData.EntityType)
                {
                    case EntityType.Application when repairData.ApplicationName != RepairConstants.SystemAppName:
                    case EntityType.Replica:
                        maxWaitForHealthStateOk = repairData.RepairPolicy.MaxTimePostRepairHealthCheck > TimeSpan.MinValue
                            ? repairData.RepairPolicy.MaxTimePostRepairHealthCheck
                            : TimeSpan.FromMinutes(10);
                        break;

                    case EntityType.Application when repairData.ApplicationName == RepairConstants.SystemAppName && repairData.RepairPolicy.RepairAction == RepairActionType.RestartProcess:
                        maxWaitForHealthStateOk = repairData.RepairPolicy.MaxTimePostRepairHealthCheck > TimeSpan.MinValue
                           ? repairData.RepairPolicy.MaxTimePostRepairHealthCheck
                           : TimeSpan.FromMinutes(5);
                        break;

                    case EntityType.Application when repairData.ApplicationName == RepairConstants.SystemAppName && repairData.RepairPolicy.RepairAction == RepairActionType.RestartFabricNode:
                        maxWaitForHealthStateOk = repairData.RepairPolicy.MaxTimePostRepairHealthCheck > TimeSpan.MinValue
                            ? repairData.RepairPolicy.MaxTimePostRepairHealthCheck
                            : TimeSpan.FromMinutes(30);
                        break;

                    case EntityType.Service:
                        maxWaitForHealthStateOk = repairData.RepairPolicy.MaxTimePostRepairHealthCheck > TimeSpan.MinValue
                            ? repairData.RepairPolicy.MaxTimePostRepairHealthCheck
                            : TimeSpan.FromMinutes(10);
                        break;

                    case EntityType.Node:
                        maxWaitForHealthStateOk = repairData.RepairPolicy.MaxTimePostRepairHealthCheck > TimeSpan.MinValue
                            ? repairData.RepairPolicy.MaxTimePostRepairHealthCheck
                            : TimeSpan.FromMinutes(30);
                        break;

                    case EntityType.Partition:
                        maxWaitForHealthStateOk = repairData.RepairPolicy.MaxTimePostRepairHealthCheck > TimeSpan.MinValue
                            ? repairData.RepairPolicy.MaxTimePostRepairHealthCheck
                            : TimeSpan.FromMinutes(15);
                        break;

                    case EntityType.Disk:
                        maxWaitForHealthStateOk = repairData.RepairPolicy.MaxTimePostRepairHealthCheck > TimeSpan.MinValue
                            ? repairData.RepairPolicy.MaxTimePostRepairHealthCheck
                            : TimeSpan.FromSeconds(5);
                        break;

                    case EntityType.Machine:
                        maxWaitForHealthStateOk = repairData.RepairPolicy.MaxTimePostRepairHealthCheck > TimeSpan.MinValue
                            ? repairData.RepairPolicy.MaxTimePostRepairHealthCheck
                            : TimeSpan.FromMinutes(60);
                        break;

                    default:
                        throw new ArgumentException("Unknown repair target type.");
                }

                // Check healthstate of repair target to see if the repair worked.
                bool isHealthy = await IsRepairTargetHealthyAfterCompletedRepair(repairData, maxWaitForHealthStateOk, cancellationToken);

                if (isHealthy)
                {
                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                            $"{repairData.RepairPolicy.RepairAction} repair for {repairTarget} has succeeded.",
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);
                }
                else
                {
                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                            $"{repairData.RepairPolicy.RepairAction} repair for {repairTarget} has failed. {repairTarget} is still in an unhealthy state.",
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);
                }

                // Tell RM we are ready to move to Completed state as our custom code has completed its repair execution successfully.
                // This is done by setting the repair task to Restoring State with ResultStatus Succeeded. RM will then move forward to Restoring
                // (and do any restoring health checks if specified), then Complete the repair job.
                _ = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                   () => FabricRepairTasks.CompleteCustomActionRepairJobAsync(
                                                            repairTask,
                                                            Context, 
                                                            cancellationToken),
                                                   cancellationToken);

                // Let RM catch up.
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                return isHealthy;
            }

            // Executor failure. Cancel repair task.
            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync_ExecuteFailed",
                    $"Executor failed for repair {repairTask.TaskId}. See logs for details. Cancelling repair task.",
                    cancellationToken,
                    repairData,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

            await FabricRepairTasks.CancelRepairTaskAsync(repairTask);
            return false;
        }

        // Support for GetHealthEventHistoryPredicateType, which enables time-scoping logic rules based on health events related to specific SF entities/targets.
        internal int GetEntityHealthEventCountWithinTimeRange(string property, TimeSpan timeWindow)
        {
            int count = 0;
            var healthEvents = DetectedHealthEvents.Where(evt => evt.HealthInformation.Property == property);

            if (healthEvents == null || !healthEvents.Any())
            {
                return count;
            }

            foreach (HealthEvent healthEvent in healthEvents)
            {
                if (DateTime.UtcNow.Subtract(healthEvent.SourceUtcTimestamp) > timeWindow)
                {
                    continue;
                }
                count++;
            }

            // Lifetime management of Health Events list data. Data is kept in-memory only for 2 days. If FH process restarts, data is not preserved.
            if (DateTime.UtcNow.Subtract(LastHealthEventsListClearDateTime) >= MaxLifeTimeHealthEventsData)
            {
                DetectedHealthEvents.Clear();
                LastHealthEventsListClearDateTime = DateTime.UtcNow;
            }

            return count;
        }

        /// <summary>
        /// This function checks to see if the target of a repair is healthy after the repair task completed. 
        /// This will signal the result via telemetry and as a health event.
        /// </summary>
        /// <param name="repairData">repairData instance.</param>
        /// <param name="maxTimeToWait">Amount of time to wait for target entity to become healthy after repair operation.</param>
        /// <param name="token">CancellationToken instance.</param>
        /// <returns>Boolean representing whether the repair target is healthy after a completed repair operation.</returns>
        private async Task<bool> IsRepairTargetHealthyAfterCompletedRepair(TelemetryData repairData, TimeSpan maxTimeToWait, CancellationToken token)
        {
            if (repairData == null)
            {
                return false;
            }

            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed <= maxTimeToWait)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (await GetCurrentAggregatedHealthStateAsync(repairData, token) == HealthState.Ok)
                {
                    stopwatch.Stop();
                    return true;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }

            stopwatch.Stop();
            return false;
        }

        /// <summary>
        /// Determines aggregated health state for repair target in supplied repair configuration.
        /// </summary>
        /// <param name="repairData">repairData instance.</param>
        /// <param name="token">CancellationToken instance.</param>
        /// <returns></returns>
        private async Task<HealthState> GetCurrentAggregatedHealthStateAsync(TelemetryData repairData, CancellationToken token)
        {
            switch (repairData.EntityType)
            {
                case EntityType.Application:
                
                    var appHealth = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                             () => FabricHealerManager.FabricClientSingleton.HealthManager.GetApplicationHealthAsync(
                                                        new Uri(repairData.ApplicationName),
                                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                        token),
                                             token);

                    bool isTargetAppHealedOnTargetNode = false;

                    // System Service repairs (process restarts)
                    if (repairData.ApplicationName == RepairConstants.SystemAppName)
                    {
                        isTargetAppHealedOnTargetNode = appHealth.HealthEvents.Any(
                            h => JsonSerializationUtility.TryDeserialize(
                                    h.HealthInformation.Description,
                                    out TelemetryData repairData)
                                        && repairData.NodeName == repairData.NodeName
                                        && repairData.ProcessName == repairData.ProcessName
                                        && repairData.HealthState == HealthState.Ok);
                    }
                    else // Application repairs (code package restarts)
                    {
                        isTargetAppHealedOnTargetNode = appHealth.HealthEvents.Any(
                            h => JsonSerializationUtility.TryDeserialize(
                                    h.HealthInformation.Description,
                                    out TelemetryData repairData)
                                        && repairData.NodeName == repairData.NodeName
                                        && repairData.ApplicationName == repairData.ApplicationName
                                        && repairData.HealthState == HealthState.Ok);
                    }

                    return isTargetAppHealedOnTargetNode ? HealthState.Ok : appHealth.AggregatedHealthState;
                    
                case EntityType.Service:

                    var serviceHealth = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                () => FabricHealerManager.FabricClientSingleton.HealthManager.GetServiceHealthAsync(
                                                        new Uri(repairData.ServiceName),
                                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                        token),
                                                token);

                    bool isTargetServiceHealedOnTargetNode = serviceHealth.HealthEvents.Any(
                               h => JsonSerializationUtility.TryDeserialize(
                                       h.HealthInformation.Description,
                                       out TelemetryData repairData)
                                           && repairData.NodeName == repairData.NodeName
                                           && repairData.ServiceName == repairData.ServiceName
                                           && repairData.HealthState == HealthState.Ok);
                    return isTargetServiceHealedOnTargetNode ? HealthState.Ok : serviceHealth.AggregatedHealthState;

                case EntityType.Node:
                case EntityType.Machine:
                
                    var nodeHealth = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                () => FabricHealerManager.FabricClientSingleton.HealthManager.GetNodeHealthAsync(
                                                            repairData.NodeName,
                                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                            token),
                                                token);

                    bool isTargetNodeHealed = nodeHealth.HealthEvents.Any(
                                                h => JsonSerializationUtility.TryDeserialize(
                                                        h.HealthInformation.Description,
                                                        out TelemetryData repairData)
                                                        && repairData.NodeName == repairData.NodeName
                                                        && repairData.HealthState == HealthState.Ok);

                    return isTargetNodeHealed ? HealthState.Ok : nodeHealth.AggregatedHealthState;
                   
                case EntityType.Replica:
                
                    // Make sure the Partition where the restarted replica was located is now healthy.
                    var partitionHealth = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                    () => FabricHealerManager.FabricClientSingleton.HealthManager.GetPartitionHealthAsync(
                                                                (Guid)repairData.PartitionId,
                                                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                token),
                                                    token);

                    return partitionHealth.AggregatedHealthState;
                    
                default:
                    return HealthState.Unknown;
            }
        }
    }
}
