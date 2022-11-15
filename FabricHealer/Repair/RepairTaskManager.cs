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
using System.Fabric.Description;

namespace FabricHealer.Repair
{
    public sealed class RepairTaskManager : IRepairTasks
    {
        private static readonly TimeSpan MaxWaitTimeForInfrastructureRepairTaskCompleted = TimeSpan.FromHours(8);
        private static readonly TimeSpan MaxWaitTimeForFHRepairTaskCompleted = TimeSpan.FromHours(1);
        private readonly RepairTaskEngine repairTaskEngine;
        private readonly RepairExecutor repairExecutor;
        private readonly TimeSpan asyncTimeout = TimeSpan.FromSeconds(60);
        private readonly DateTime healthEventsListCreationTime = DateTime.UtcNow;
        private readonly TimeSpan maxLifeTimeHealthEventsData = TimeSpan.FromDays(2);
        private DateTime lastHealthEventsListClearDateTime;
        internal readonly List<(string entityName, HealthEvent healthEvent)> detectedHealthEvents;

        public RepairTaskManager()
        {
            repairExecutor = new RepairExecutor();
            repairTaskEngine = new RepairTaskEngine();
            detectedHealthEvents = new List<(string id, HealthEvent healthEvent)>();
            lastHealthEventsListClearDateTime = healthEventsListCreationTime;
        }

        public async Task RemoveServiceFabricNodeStateAsync(string nodeName, CancellationToken cancellationToken)
        {
            await FabricHealerManager.FabricClientSingleton.ClusterManager.RemoveNodeStateAsync(nodeName, asyncTimeout, cancellationToken);
        }

        public async Task ActivateServiceFabricNodeAsync(string nodeName, CancellationToken cancellationToken)
        {
            await FabricHealerManager.FabricClientSingleton.ClusterManager.ActivateNodeAsync(nodeName, asyncTimeout, cancellationToken);
        }

        public async Task<bool> SafeRestartServiceFabricNodeAsync(TelemetryData repairData, RepairTask repairTask, CancellationToken cancellationToken)
        {
            if (!await repairExecutor.SafeRestartFabricNodeAsync(repairData, repairTask, cancellationToken))
            {
                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "SafeRestartFabricNodeAsync",
                        $"Did not restart Fabric node {repairData.NodeName}",
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }

            await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
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
                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
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
                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
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
            functorTable.Add(CheckInsideRunIntervalPredicateType.Singleton(RepairConstants.CheckInsideRunInterval, repairData));
            functorTable.Add(CheckInsideProbationPeriodType.Singleton(RepairConstants.CheckInsideProbationPeriod, repairData));
            functorTable.Add(CheckInsideScheduleIntervalPredicateType.Singleton(RepairConstants.CheckInsideScheduleInterval, repairData));
            functorTable.Add(CheckOutstandingRepairsPredicateType.Singleton(RepairConstants.CheckOutstandingRepairs, repairData));
            functorTable.Add(EmitMessagePredicateType.Singleton(RepairConstants.EmitMessage));
            functorTable.Add(CheckEntityHealthStateDurationPredicateType.Singleton(RepairConstants.CheckInsideHealthStateMinDuration, repairData, this));
            functorTable.Add(GetHealthEventHistoryPredicateType.Singleton(RepairConstants.GetHealthEventHistory, this, repairData));
            functorTable.Add(GetRepairHistoryPredicateType.Singleton(RepairConstants.GetRepairHistory, repairData));

            // Add external repair predicates.
            functorTable.Add(DeleteFilesPredicateType.Singleton(RepairConstants.DeleteFiles, this, repairData));
            functorTable.Add(RestartCodePackagePredicateType.Singleton(RepairConstants.RestartCodePackage, this, repairData));
            functorTable.Add(RestartFabricNodePredicateType.Singleton(RepairConstants.RestartFabricNode, this, repairExecutorData, repairTaskEngine, repairData));
            functorTable.Add(RestartFabricSystemProcessPredicateType.Singleton(RepairConstants.RestartFabricSystemProcess, this, repairData));
            functorTable.Add(RestartReplicaPredicateType.Singleton(RepairConstants.RestartReplica, this, repairData));
            functorTable.Add(ScheduleMachineRepairPredicateType.Singleton(RepairConstants.ScheduleMachineRepair, this, repairData));

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
            compoundTerm.AddArgument(new Constant(repairData.EntityType.ToString()), RepairConstants.EntityType);
            compoundTerm.AddArgument(new Constant(repairData.HealthState.ToString()), RepairConstants.HealthState);
            compoundTerm.AddArgument(new Constant(repairData.Metric), RepairConstants.MetricName);
            compoundTerm.AddArgument(new Constant(Convert.ToInt64(repairData.Value)), RepairConstants.MetricValue);
            compoundTerm.AddArgument(new Constant(repairData.NodeName), RepairConstants.NodeName);
            compoundTerm.AddArgument(new Constant(repairData.NodeType), RepairConstants.NodeType);
            compoundTerm.AddArgument(new Constant(repairData.ObserverName), RepairConstants.ObserverName);
            compoundTerm.AddArgument(new Constant(repairData.OS), RepairConstants.OS);
            compoundTerm.AddArgument(new Constant(repairData.ServiceKind), RepairConstants.ServiceKind);
            compoundTerm.AddArgument(new Constant(repairData.ServiceName), RepairConstants.ServiceName);
            compoundTerm.AddArgument(new Constant(repairData.ProcessId), RepairConstants.ProcessId);
            compoundTerm.AddArgument(new Constant(repairData.ProcessName), RepairConstants.ProcessName);
            compoundTerm.AddArgument(new Constant(repairData.ProcessStartTime), RepairConstants.ProcessStartTime);
            compoundTerm.AddArgument(new Constant(repairData.PartitionId), RepairConstants.PartitionId);
            compoundTerm.AddArgument(new Constant(repairData.ReplicaId), RepairConstants.ReplicaOrInstanceId);
            compoundTerm.AddArgument(new Constant(repairData.ReplicaRole), RepairConstants.ReplicaRole);
            compoundTerms.Add(compoundTerm);

            // Run Guan query.
            // This is where the supplied rules are run with FO data that may or may not lead to mitigation of some supported SF entity in trouble (or a VM/Disk).
            await queryDispatcher.RunQueryAsync(compoundTerms);
        }

        // The repair will be executed by SF Infrastructure service, not FH. This is the case for all
        // Machine-level repairs.
        public async Task<bool> ScheduleInfrastructureRepairTask(TelemetryData repairData, CancellationToken cancellationToken)
        {
            // Internal throttling to protect against bad rules (over scheduling of repair tasks within a fixed time range). 
            if (await RepairCountThrottleMaxCheck(repairData, cancellationToken))
            {
                string message = $"Too many repairs of this type have been scheduled in the last 1 hour: " +
                                 $"{repairData.RepairPolicy.InfrastructureRepairName}. Will not schedule another repair at this time.";

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"InternalThrottling({repairData.NodeName}::{repairData.RepairPolicy.InfrastructureRepairName})",
                        message,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }

            // Create repair task for target node.
            var repairTask = await FabricRepairTasks.CreateRepairTaskAsync(repairData, null, RepairTaskEngine.InfraTaskIdPrefix, cancellationToken);

            if (repairTask == null)
            {
                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                         LogLevel.Info,
                         "ScheduleInfrastructureRepairTask::Failure",
                         "Unable to schedule Infrastructure Repair Task.",
                         cancellationToken,
                         repairData,
                         FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }

            await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "ScheduleInfrastructureRepairTask::Success",
                    $"Successfully scheduled Infrastructure Repair Task {repairTask.TaskId}",
                    cancellationToken,
                    repairData,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

            return true;
        }

        private static async Task<bool> RepairCountThrottleMaxCheck(TelemetryData repairData, CancellationToken cancellationToken)
        {
            string repairPolicySectionName;

            switch (repairData.EntityType)
            {
                // App/Service repair (user).
                case EntityType.Application when repairData.ApplicationName.ToLower() != RepairConstants.SystemAppName.ToLower():
                case EntityType.Service:
                case EntityType.StatefulService:
                case EntityType.StatelessService:
                    repairPolicySectionName = RepairConstants.AppRepairPolicySectionName;
                    break;

                // System service process repair.
                case EntityType.Application when repairData.ProcessName != null:
                case EntityType.Process:
                    repairPolicySectionName = RepairConstants.SystemServiceRepairPolicySectionName;
                    break;

                // Disk repair.
                case EntityType.Disk when FabricHealerManager.ServiceContext.NodeContext.NodeName == repairData.NodeName:
                    repairPolicySectionName = RepairConstants.DiskRepairPolicySectionName;
                    break;

                // Machine repair.
                case EntityType.Machine:
                    repairPolicySectionName = RepairConstants.MachineRepairPolicySectionName;
                    break;

                // Fabric Node repair.
                case EntityType.Node:
                    repairPolicySectionName = RepairConstants.FabricNodeRepairPolicySectionName;
                    break;

                default:
                    return false;
            }

            string throttleSetting = FabricHealerManager.GetSettingParameterValue(repairPolicySectionName, RepairConstants.MaxRepairsInTimeRange);

            // <Parameter Name="MaxRepairsInTimeRange" Value="5, 02:00:00" />
            // <Parameter Name="MaxRepairsInTimeRange" Value="5, 02:00:00; 6, 24:00:00; 7, 48:00:00;" />
            if (throttleSetting.Contains(';'))
            {
                string[] arrSettings = throttleSetting.Split(';', StringSplitOptions.RemoveEmptyEntries);

                foreach (string s in arrSettings)
                {
                    string[] settings = s.Split(',', StringSplitOptions.RemoveEmptyEntries);

                    if (settings.Length == 0)
                    {
                        continue;
                    }

                    if (!int.TryParse(settings[0].Trim(), out int maxCount))
                    {
                        throw new ArgumentException($"Unsupported value for maxCount specified in {repairPolicySectionName} setting. Please check your configuration.");
                    }

                    if (!TimeSpan.TryParse(settings[1].Trim(), out TimeSpan timeRange))
                    {
                        throw new ArgumentException($"Unsupported value timeRange in {repairPolicySectionName} setting. Please check your configuration.");
                    }

                    if (await FabricRepairTasks.GetCreatedRepairCountWithinTimeRangeAsync(timeRange, repairData, cancellationToken) >= maxCount)
                    {
                        return true;
                    }
                }
            }
            else
            {
                string[] settings = throttleSetting.Split(',', StringSplitOptions.RemoveEmptyEntries);

                if (settings.Length == 0)
                {
                    return false;
                }

                if (!int.TryParse(settings[0].Trim(), out int maxCount))
                {
                    throw new ArgumentException($"Unsupported value for maxCount specified in {repairPolicySectionName} setting. Please check your configuration.");
                }

                if (!TimeSpan.TryParse(settings[1].Trim(), out TimeSpan timeRange))
                {
                    throw new ArgumentException($"Unsupported value timeRange in {repairPolicySectionName} setting. Please check your configuration.");
                }

                return await FabricRepairTasks.GetCreatedRepairCountWithinTimeRangeAsync(timeRange, repairData, cancellationToken) >= maxCount;
            }

            return false;
        }

        public async Task<bool> DeleteFilesAsyncAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            return await repairExecutor.DeleteFilesAsync(
                            repairData ?? throw new ArgumentException(nameof(repairData)),
                            cancellationToken);
        }

        public async Task<bool> RestartReplicaAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            return await repairExecutor.RestartReplicaAsync(
                            repairData ?? throw new ArgumentException("repairData can't be null."),
                            cancellationToken);
        }

        public async Task<bool> RemoveReplicaAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            return await repairExecutor.RemoveReplicaAsync(
                            repairData ?? throw new ArgumentException("repairData can't be null."),
                            cancellationToken);
        }

        public async Task<bool> RestartDeployedCodePackageAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            var result = await repairExecutor.RestartDeployedCodePackageAsync(
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
            if (repairData.NodeName != FabricHealerManager.ServiceContext.NodeContext.NodeName)
            {
                return false;
            }

            string actionMessage =
               $"Attempting to restart Service Fabric system process {repairData.ProcessName}.";

            await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                     LogLevel.Info,
                     "RepairExecutor.RestartSystemServiceProcessAsync::Start",
                     actionMessage,
                     cancellationToken,
                     repairData,
                     FabricHealerManager.ConfigSettings.EnableVerboseLogging);

            bool result = await repairExecutor.RestartSystemServiceProcessAsync(repairData, cancellationToken);

            if (!result)
            {
                return false;
            }

            string statusSuccess = $"Successfully restarted Service Fabric system service process {repairData.ProcessName} on node {repairData.NodeName}.";

            await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
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
                var nodes = await FabricHealerManager.FabricClientSingleton.QueryManager.GetNodeListAsync(nodeName, asyncTimeout, cancellationToken);
                return nodes.Count > 0 ? nodes[0] : null;
            }
            catch (Exception e) when (e is FabricException || e is TaskCanceledException || e is TimeoutException)
            {
                FabricHealerManager.RepairLogger.LogError($"Error getting node {nodeName}:{Environment.NewLine}{e}");
                return null;
            }
        }

        public async Task<RepairTask> ScheduleFabricHealerRepairTaskAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            await Task.Delay(new Random().Next(500, 1500), cancellationToken);

            // Internal throttling to protect against bad rules (over-scheduling of repair tasks within a fixed time range). 
            if (await RepairCountThrottleMaxCheck(repairData, cancellationToken))
            {
                string message = $"Too many repairs of this type have been scheduled in the last 15 minutes: " +
                                 $"{repairData.RepairPolicy.RepairId}. Will not schedule another repair at this time.";

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"InternalThrottling({repairData.RepairPolicy.RepairId})",
                        message,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return null;
            }

            // Has the repair already been scheduled?
            if (await repairTaskEngine.IsRepairInProgressAsync(RepairTaskEngine.FHTaskIdPrefix, repairData, cancellationToken))
            {
                return null;
            }

            // Don't attempt a node-level repair on a node where there is already an active node-level repair.
            if (await repairTaskEngine.IsNodeLevelRepairCurrentlyInFlightAsync(repairData, cancellationToken))
            {
                string message = $"Node {repairData.NodeName} already has a node-impactful repair in progress: " +
                                 $"{repairData.RepairPolicy.RepairAction}";

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"NodeRepairAlreadyInProgress::{repairData.NodeName}",
                        message,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return null;
            }

            var executorData = new RepairExecutorData
            {
                ExecutorTimeoutInMinutes = (int)MaxWaitTimeForFHRepairTaskCompleted.TotalMinutes,
                RepairData = repairData
            };

            // Create custom FH repair task for target node.
            var repairTask = await FabricRepairTasks.CreateRepairTaskAsync(
                                    repairData,
                                    executorData,
                                    RepairTaskEngine.FHTaskIdPrefix,
                                    cancellationToken);
            return repairTask;
        }

        public async Task<bool> ExecuteFabricHealerRepairTaskAsync(RepairTask repairTask, TelemetryData repairData, CancellationToken cancellationToken)
        {
            if (repairTask == null)
            {
                return false;
            }

            TimeSpan approvalTimeout = TimeSpan.FromMinutes(10);
            Stopwatch stopWatch = Stopwatch.StartNew();
            bool isApproved = false;

            var repairs =
                await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairConstants.FabricHealer, cancellationToken);

            if (repairs.All(repair => repair.TaskId != repairTask.TaskId))
            {
                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                         LogLevel.Info,
                         "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                         $"Failed to find scheduled repair task {repairTask.TaskId}.",
                         FabricHealerManager.Token,
                         repairData,
                         FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }

            await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                     LogLevel.Info,
                     "RepairTaskManager::WaitingForApproval",
                     $"Waiting for RM to Approve repair task {repairTask.TaskId}.",
                     cancellationToken,
                     repairData,
                     FabricHealerManager.ConfigSettings.EnableVerboseLogging);

            while (approvalTimeout >= stopWatch.Elapsed)
            {
                repairs = await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairConstants.FabricHealer, cancellationToken);

                // Was repair cancelled (or cancellation requested) by another FH instance for some reason? Could be due to FH going down or a new deployment or a bug (fix it...).
                if (repairs.Any(repair => repair.TaskId == repairTask.TaskId
                                       && (repair.State == RepairTaskState.Completed && repair.ResultStatus == RepairTaskResult.Cancelled
                                           || repair.Flags == RepairTaskFlags.CancelRequested || repair.Flags == RepairTaskFlags.AbortRequested)))
                {
                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                             LogLevel.Info,
                             "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                             $"Repair Task {repairTask.TaskId} was aborted or cancelled.",
                             FabricHealerManager.Token,
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
                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                         LogLevel.Info,
                         "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync_Approved",
                         $"RM has Approved repair task {repairTask.TaskId}.",
                         cancellationToken,
                         repairData,
                         FabricHealerManager.ConfigSettings.EnableVerboseLogging);
            }
            else
            {
                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
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

            await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
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
                                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
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
                                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
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
                                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync::NoReplica",
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
                                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
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
                                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                         LogLevel.Info,
                                         "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync::NoReplica",
                                         $"Stateful replica {repairData.ReplicaId} not found on partition " +
                                         $"{repairData.PartitionId}.",
                                         cancellationToken,
                                         repairData,
                                         FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                                break;
                            }

                            var replica = repList[0];

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

                                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
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
            string repairTarget = null;

            switch (repairData.EntityType)
            {
                // Try and handle the case where EntityType is not specified (facts from FHProxy, for example)
                // or is explicitly set to Invalid for some reason.
                case EntityType.Unknown:

                    if (!string.IsNullOrWhiteSpace(repairData.ServiceName))
                    {
                        goto case EntityType.Service;
                    }
                    else if (!string.IsNullOrWhiteSpace(repairData.ApplicationName))
                    {
                        goto case EntityType.Application;
                    }
                    else if (!string.IsNullOrWhiteSpace(repairData.NodeName))
                    {
                        goto case EntityType.Node;
                    }
                    else if (repairData.ReplicaId > 0)
                    {
                        goto case EntityType.Replica;
                    }
                    else if (!string.IsNullOrWhiteSpace(repairData.ProcessName) || repairData.ProcessId > 0)
                    {
                        goto case EntityType.Process;
                    }
                    else
                    {
                        return false;
                    }

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

                    default:
                        throw new ArgumentException("Unsupported repair target type.");
                }

                // Check healthstate of repair target to see if the repair worked.
                bool isHealthy = await IsRepairTargetHealthyAfterCompletedRepair(repairData, maxWaitForHealthStateOk, cancellationToken);

                if (isHealthy)
                {
                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                             LogLevel.Info,
                             "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                             $"{repairData.RepairPolicy.RepairAction} repair for {repairTarget} has succeeded.",
                             cancellationToken,
                             repairData,
                             FabricHealerManager.ConfigSettings.EnableVerboseLogging);
                }
                else
                {
                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
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
                                    cancellationToken),
                            cancellationToken);

                // Let RM catch up.
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                return isHealthy;
            }

            // Executor failure. Cancel repair task.
            await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
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
        internal int GetEntityHealthEventCountWithinTimeRange(TelemetryData repairData, TimeSpan timeWindow)
        {
            int count = 0;
            if (repairData == null || detectedHealthEvents == null || !detectedHealthEvents.Any())
            {
                return count;
            }

            string id = string.Empty;

            switch (repairData.EntityType)
            {
                case EntityType.Application:
                    id = repairData.ApplicationName;
                    break;

                case EntityType.Service:
                    id = repairData.ServiceName;
                    break;

                case EntityType.Disk:
                case EntityType.Machine:
                case EntityType.Node:
                    id = repairData.NodeName;
                    break;
            }

            var entityHealthEvents = detectedHealthEvents.Where(
                    evt => evt.entityName == id && evt.healthEvent.HealthInformation.Property == repairData.Property);

            foreach (var (_, healthEvent) in entityHealthEvents)
            {
                if (DateTime.UtcNow.Subtract(healthEvent.SourceUtcTimestamp) > timeWindow)
                {
                    continue;
                }
                count++;
            }

            // Lifetime management of Health Events list data. Data is kept in-memory only for 2 days. If FH process restarts, data is not preserved.
            if (DateTime.UtcNow.Subtract(lastHealthEventsListClearDateTime) >= maxLifeTimeHealthEventsData)
            {
                detectedHealthEvents.Clear();
                lastHealthEventsListClearDateTime = DateTime.UtcNow;
            }

            return count;
        }

        /// <summary>
        /// Returns the anount of time the target entity (application, node, etc) has been in the specified health state.
        /// </summary>
        /// <param name="entityType">EntityType</param>
        /// <param name="nameOrIdFilter">String representation of the target entity's name or ID (e.g., application name or node name or partition id)</param>
        /// <param name="healthState">Target HealthState to match.</param>
        /// <param name="token">CancellationToken</param>
        /// <returns></returns>
        internal async Task<TimeSpan> GetEntityCurrentHealthStateDurationAsync(
                                                TelemetryData repairData,
                                                TimeSpan timeWindow,
                                                CancellationToken token)
        {
            HealthEventsFilter healthEventsFilter = new HealthEventsFilter();

            if (repairData.HealthState == HealthState.Warning)
            {
                healthEventsFilter.HealthStateFilterValue = HealthStateFilter.Warning;
            }
            else if (repairData.HealthState == HealthState.Error)
            {
                healthEventsFilter.HealthStateFilterValue = HealthStateFilter.Error;
            }
            else if (repairData.HealthState == HealthState.Ok)
            {
                healthEventsFilter.HealthStateFilterValue = HealthStateFilter.Ok;
            }
            else
            {
                healthEventsFilter.HealthStateFilterValue = HealthStateFilter.None;
            }

            switch (repairData.EntityType)
            {
                case EntityType.Application:

                    var appqueryDesc = new ApplicationHealthQueryDescription(new Uri(repairData.ApplicationName))
                    {
                        EventsFilter = healthEventsFilter
                    };

                    try
                    {
                        var appHealth =
                            await FabricHealerManager.FabricClientSingleton.HealthManager.GetApplicationHealthAsync(
                                        appqueryDesc, FabricHealerManager.ConfigSettings.AsyncTimeout, token);

                        if (appHealth == null || appHealth.HealthEvents.Count == 0)
                        {
                            return TimeSpan.MinValue;
                        }

                        // How many times has the entity been put into Error health state in the last 2 hours?
                        if (healthEventsFilter.HealthStateFilterValue == HealthStateFilter.Error)
                        {
                            if (GetEntityHealthEventCountWithinTimeRange(repairData, timeWindow) > 1)
                            {
                                var orderedEvents = detectedHealthEvents.Where(
                                        evt => evt.entityName == repairData.ApplicationName &&
                                               evt.healthEvent.HealthInformation.Property == repairData.Property)
                                      .OrderByDescending(o => o.healthEvent.SourceUtcTimestamp);

                                return DateTime.UtcNow.Subtract(
                                    orderedEvents.Last().healthEvent.SourceUtcTimestamp);
                            }
                        }

                        var appHealthEvents = appHealth.HealthEvents.OrderByDescending(o => o.SourceUtcTimestamp);

                        // return the time since the last health event was issued, as a TimeSpan.
                        return DateTime.UtcNow.Subtract(appHealthEvents.First().SourceUtcTimestamp);

                    }
                    catch (FabricException)
                    {
                        return TimeSpan.MinValue;
                    }

                case EntityType.Service:

                    var servicequeryDesc = new ServiceHealthQueryDescription(new Uri(repairData.ServiceName))
                    {
                        EventsFilter = healthEventsFilter
                    };

                    try
                    {
                        var serviceHealth =
                            await FabricHealerManager.FabricClientSingleton.HealthManager.GetServiceHealthAsync(
                                        servicequeryDesc, FabricHealerManager.ConfigSettings.AsyncTimeout, token);

                        if (serviceHealth == null || serviceHealth.HealthEvents.Count == 0)
                        {
                            return TimeSpan.MinValue;
                        }

                        // How many times has the entity been put into Error health state in the last 2 hours ?
                        if (healthEventsFilter.HealthStateFilterValue == HealthStateFilter.Error)
                        {
                            if (GetEntityHealthEventCountWithinTimeRange(repairData, timeWindow) > 1)
                            {
                                var orderedEvents = detectedHealthEvents.Where(
                                        evt => evt.entityName == repairData.ServiceName &&
                                               evt.healthEvent.HealthInformation.Property == repairData.Property)
                                      .OrderByDescending(o => o.healthEvent.SourceUtcTimestamp);

                                return DateTime.UtcNow.Subtract(orderedEvents.Last().healthEvent.SourceUtcTimestamp);
                            }
                        }

                        var serviceHealthEvents = serviceHealth.HealthEvents.OrderByDescending(o => o.SourceUtcTimestamp);
                        return DateTime.UtcNow.Subtract(serviceHealthEvents.First().SourceUtcTimestamp);
                    }
                    catch (FabricException)
                    {
                        return TimeSpan.MinValue;
                    }

                case EntityType.Disk:
                case EntityType.Machine:
                case EntityType.Node:

                    var nodequeryDesc = new NodeHealthQueryDescription(repairData.NodeName)
                    {
                        EventsFilter = healthEventsFilter
                    };

                    try
                    {
                        var nodeHealth =
                            await FabricHealerManager.FabricClientSingleton.HealthManager.GetNodeHealthAsync(
                                        nodequeryDesc, FabricHealerManager.ConfigSettings.AsyncTimeout, token);

                        if (nodeHealth == null || nodeHealth.HealthEvents.Count == 0)
                        {
                            return TimeSpan.MinValue;
                        }

                        // How many times has the entity been put into Error health state in the last 2 hours?
                        if (healthEventsFilter.HealthStateFilterValue == HealthStateFilter.Error)
                        {
                            if (GetEntityHealthEventCountWithinTimeRange(repairData, timeWindow) > 1)
                            {
                                var orderedEvents = detectedHealthEvents.Where(
                                        evt => evt.entityName == repairData.NodeName &&
                                               evt.healthEvent.HealthInformation.Property == repairData.Property)
                                      .OrderByDescending(o => o.healthEvent.SourceUtcTimestamp);

                                return DateTime.UtcNow.Subtract(orderedEvents.Last().healthEvent.SourceUtcTimestamp);
                            }
                        }

                        var nodeHealthEvents = nodeHealth.HealthEvents.OrderByDescending(o => o.SourceUtcTimestamp);
                        return DateTime.UtcNow.Subtract(nodeHealthEvents.First().SourceUtcTimestamp);
                    }
                    catch (Exception e) when (e is ArgumentException || e is FabricException || e is InvalidOperationException || e is TaskCanceledException || e is TimeoutException)
                    {
                        string message = $"Unable to get {repairData.HealthState} health state duration for {repairData.EntityType}: {e.Message}";
                        FabricHealerManager.RepairLogger.LogWarning(message);
                        return TimeSpan.MinValue;
                    }

                default:
                    return TimeSpan.MinValue;
            }
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
                            h => JsonSerializationUtility.TryDeserializeObject(
                                    h.HealthInformation.Description,
                                    out TelemetryData repairData)
                                        && repairData.NodeName == repairData.NodeName
                                        && repairData.ProcessName == repairData.ProcessName
                                        && repairData.HealthState == HealthState.Ok);
                    }
                    else // Application repairs (code package restarts)
                    {
                        isTargetAppHealedOnTargetNode = appHealth.HealthEvents.Any(
                            h => JsonSerializationUtility.TryDeserializeObject(
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
                               h => JsonSerializationUtility.TryDeserializeObject(
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
                                                h => JsonSerializationUtility.TryDeserializeObject(
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