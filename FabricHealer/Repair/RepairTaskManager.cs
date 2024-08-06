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
using Guan.Logic;
using FabricHealer.Repair.Guan;
using FabricHealer.Utilities;
using Module = Guan.Logic.Module;
using FabricHealer.Interfaces;

namespace FabricHealer.Repair
{
    public sealed class RepairTaskManager
    {
        private static readonly TimeSpan MaxLifeTimeHealthEventsData = TimeSpan.FromHours(8);
        private static DateTime LastHealthEventsListClearDateTime = DateTime.UtcNow;
        internal static readonly List<HealthEventData> DetectedHealthEvents = [];

        // this API can be used by the plugins to access the inmemory health events
        public static IEnumerable<HealthEventData> GetDetectedHealthEvents()
        {
            return DetectedHealthEvents.AsReadOnly();
        }

        public static async Task StartRepairWorkflowAsync(TelemetryData repairData, List<string> repairRules, CancellationToken cancellationToken, string serializedRepairData = "")
        {
            if (await RepairTaskEngine.HasActiveStopFHRepairJob(cancellationToken))
            {
                return;
            }

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
                await RunGuanQueryAsync(repairData, repairRules, cancellationToken, serializedRepairData: serializedRepairData);
            }
            catch (GuanException ge)
            {
               await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        $"{repairData.Property ?? "RunGuanQueryAsync"}::GuanException",
                        $"Failure executing Guan query: {ge.Message}",
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
        public static async Task RunGuanQueryAsync(
                                    TelemetryData repairData,
                                    List<string> repairRules,
                                    CancellationToken cancellationToken,
                                    RepairExecutorData repairExecutorData = null,
                                    string serializedRepairData = "")
        {
            if (await RepairTaskEngine.HasActiveStopFHRepairJob(cancellationToken))
            {
                return;
            }

            // Add predicate types to functor table. Note that all health information data from FO are automatically passed to all predicates.
            FunctorTable functorTable = new();

            // Add external helper predicates.
            functorTable.Add(CheckFolderSizePredicateType.Singleton(RepairConstants.CheckFolderSize, repairData));
            functorTable.Add(CheckInsideRunIntervalPredicateType.Singleton(RepairConstants.CheckInsideRunInterval, repairData));
            functorTable.Add(CheckInsideNodeProbationPeriodPredicateType.Singleton(RepairConstants.CheckInsideNodeProbationPeriod, repairData));
            functorTable.Add(CheckInsideScheduleIntervalPredicateType.Singleton(RepairConstants.CheckInsideScheduleInterval, repairData));
            functorTable.Add(CheckOutstandingRepairsPredicateType.Singleton(RepairConstants.CheckOutstandingRepairs, repairData));
            functorTable.Add(LogInfoPredicateType.Singleton(RepairConstants.LogInfo));
            functorTable.Add(LogErrorPredicateType.Singleton(RepairConstants.LogError));
            functorTable.Add(LogWarningPredicateType.Singleton(RepairConstants.LogWarning));
            functorTable.Add(LogRulePredicateType.Singleton(RepairConstants.LogRule, repairData));
            functorTable.Add(CheckInsideHealthStateMinDurationPredicateType.Singleton(RepairConstants.CheckInsideHealthStateMinDuration, repairData));
            functorTable.Add(GetHealthEventHistoryPredicateType.Singleton(RepairConstants.GetHealthEventHistory, repairData));
            functorTable.Add(GetRepairHistoryPredicateType.Singleton(RepairConstants.GetRepairHistory, repairData));

            // Add external repair predicates.
            functorTable.Add(DeactivateFabricNodePredicateType.Singleton(RepairConstants.DeactivateFabricNode, repairData));
            functorTable.Add(DeleteFilesPredicateType.Singleton(RepairConstants.DeleteFiles, repairData));
            functorTable.Add(RestartCodePackagePredicateType.Singleton(RepairConstants.RestartCodePackage, repairData));
            functorTable.Add(RestartFabricNodePredicateType.Singleton(RepairConstants.RestartFabricNode, repairData));
            functorTable.Add(RestartFabricSystemProcessPredicateType.Singleton(RepairConstants.RestartFabricSystemProcess, repairData));
            functorTable.Add(RestartReplicaPredicateType.Singleton(RepairConstants.RestartReplica, repairData));
            functorTable.Add(ScheduleMachineRepairPredicateType.Singleton(RepairConstants.ScheduleMachineRepair, repairData));

            // register custom predicates.
            if (FabricHealerManager.ConfigSettings.EnableCustomRepairPredicateType)
            {
                RepairTaskManager.LoadCustomPredicateTypes(functorTable, serializedRepairData);
            }

            // Parse rules.
            Module module = Module.Parse("fh_external", repairRules, functorTable);

            // Create guan query.
            GuanQueryDispatcher queryDispatcher = new(module);

            /* Bind default arguments to goal (Mitigate). */

            List<CompoundTerm> compoundTerms = [];

            // Mitigate is the head of the rules used in FH. It's the goal that Guan will try to accomplish based on the logical expressions (or subgoals) that form a given rule.
            CompoundTerm ruleHead = new("Mitigate");

            // The type of metric that led FO to generate the unhealthy evaluation for the entity (App, Node, VM, Replica, etc).
            // We rename these for brevity for simplified use in logic rule composition (e;g., MetricName="Threads" instead of MetricName="Total Thread Count").
            repairData.Metric = SupportedErrorCodes.GetMetricNameFromErrorCode(repairData.Code);

            // These args hold the related values supplied by FO and are available anywhere Mitigate is used as a rule head.
            // Think of these as facts from FabricObserver.
            ruleHead.AddArgument(new Constant(repairData.ApplicationName), RepairConstants.AppName);
            ruleHead.AddArgument(new Constant(repairData.Code), RepairConstants.ErrorCode);
            ruleHead.AddArgument(new Constant(repairData.EntityType.ToString()), RepairConstants.EntityType);
            ruleHead.AddArgument(new Constant(repairData.HealthState.ToString()), RepairConstants.HealthState);
            ruleHead.AddArgument(new Constant(repairData.Metric), RepairConstants.MetricName);
            ruleHead.AddArgument(new Constant(Convert.ToInt64(repairData.Value)), RepairConstants.MetricValue);
            ruleHead.AddArgument(new Constant(repairData.NodeName), RepairConstants.NodeName);
            ruleHead.AddArgument(new Constant(repairData.NodeType), RepairConstants.NodeType);
            ruleHead.AddArgument(new Constant(repairData.ObserverName), RepairConstants.ObserverName);
            ruleHead.AddArgument(new Constant(repairData.OS), RepairConstants.OS);
            ruleHead.AddArgument(new Constant(repairData.ServiceKind), RepairConstants.ServiceKind);
            ruleHead.AddArgument(new Constant(repairData.ServiceName), RepairConstants.ServiceName);
            ruleHead.AddArgument(new Constant(repairData.ProcessId), RepairConstants.ProcessId);
            ruleHead.AddArgument(new Constant(repairData.ProcessName), RepairConstants.ProcessName);
            ruleHead.AddArgument(new Constant(repairData.ProcessStartTime), RepairConstants.ProcessStartTime);
            ruleHead.AddArgument(new Constant(repairData.Property), RepairConstants.Property);
            ruleHead.AddArgument(new Constant(repairData.PartitionId), RepairConstants.PartitionId);
            ruleHead.AddArgument(new Constant(repairData.ReplicaId), RepairConstants.ReplicaOrInstanceId);
            ruleHead.AddArgument(new Constant(repairData.ReplicaRole), RepairConstants.ReplicaRole);
            ruleHead.AddArgument(new Constant(repairData.Source), RepairConstants.Source);
            compoundTerms.Add(ruleHead);

            // Run Guan query.
            // This is where the supplied rules are run with FO data that may or may not lead to mitigation of some supported SF entity in trouble (or a VM/Disk).
            await queryDispatcher.RunQueryAsync(compoundTerms, cancellationToken);
        }

        private static void LoadCustomPredicateTypes(FunctorTable functorTable, string serializedRepairData)
        {
            var pluginLoader = new RepairPredicateTypePluginLoader(FabricHealerManager.RepairLogger, FabricHealerManager.ServiceContext, functorTable, serializedRepairData);
            Task.Run(async () => await pluginLoader.LoadPluginsAndCallCustomAction(typeof(RepairPredicateTypeAttribute), typeof(IRepairPredicateType))).Wait();
        }

        // The repair will be executed by SF Infrastructure service, not FH. This is the case for all
        // Machine-level repairs.
        public static async Task<bool> ScheduleInfrastructureRepairTask(TelemetryData repairData, CancellationToken cancellationToken)
        {
            if (FabricHealerManager.InstanceCount is (-1) or > 1)
            {
                await FabricHealerManager.RandomWaitAsync(cancellationToken);
            }

            if (await RepairTaskEngine.HasActiveStopFHRepairJob(cancellationToken))
            {
                return false;
            }

            // Internal throttling to protect against bad rules (over scheduling of repair tasks within a fixed time range). 
            if (await CheckRepairCountThrottle(repairData, cancellationToken))
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
            var repairTask = await FabricRepairTasks.CreateRepairTaskAsync(repairData, null, cancellationToken);

            if (repairTask == null)
            {
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

        private static async Task<bool> CheckRepairCountThrottle(TelemetryData repairData, CancellationToken cancellationToken)
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

            if (string.IsNullOrWhiteSpace(throttleSetting))
            {
                return false;
            }

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

                    if (await FabricRepairTasks.GetScheduledRepairCountWithinTimeRangeAsync(timeRange, repairData, cancellationToken) >= maxCount)
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

                return await FabricRepairTasks.GetScheduledRepairCountWithinTimeRangeAsync(timeRange, repairData, cancellationToken) >= maxCount;
            }

            return false;
        }

        public static async Task<bool> DeleteFilesAsyncAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            return await RepairExecutor.DeleteFilesAsync(
                            repairData ?? throw new ArgumentException("repairData can't be null."),
                            cancellationToken);
        }

        public static async Task<bool> RestartReplicaAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            string actionMessage = $"Attempting to restart stateful replica {repairData.ReplicaId} " +
                                   $"on partition {repairData.PartitionId} on node {repairData.NodeName}.";

            await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.RestartReplicaAsync::Start",
                    actionMessage,
                    cancellationToken,
                    repairData,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

            return await RepairExecutor.RestartReplicaAsync(
                            repairData ?? throw new ArgumentException("repairData can't be null."),
                            cancellationToken);
        }

        public static async Task<bool> RemoveReplicaAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            string actionMessage =
                $"Attempting to remove stateless instance {repairData.ReplicaId} " +
                $"on partition {repairData.PartitionId} on node {repairData.NodeName}.";

            await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.RemoveReplicaAsync::Start",
                    actionMessage,
                    cancellationToken,
                    repairData,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

            return await RepairExecutor.RemoveReplicaAsync(
                            repairData ?? throw new ArgumentException("repairData can't be null."),
                            cancellationToken);
        }

        public static async Task<bool> RestartDeployedCodePackageAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            var result = await RepairExecutor.RestartDeployedCodePackageAsync(
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
        private static async Task<bool> RestartSystemServiceProcessAsync(TelemetryData repairData, CancellationToken cancellationToken)
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

            bool result = await RepairExecutor.RestartSystemServiceProcessAsync(repairData, cancellationToken);

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

        private static async Task<Node> GetFabricNodeFromNodeNameAsync(string nodeName, CancellationToken cancellationToken)
        {
            try
            {
                var nodes = await FabricHealerManager.FabricClientSingleton.QueryManager.GetNodeListAsync(nodeName, FabricHealerManager.ConfigSettings.AsyncTimeout, cancellationToken);
                return nodes.Count > 0 ? nodes[0] : null;
            }
            catch (Exception e) when (e is FabricException or TaskCanceledException or TimeoutException)
            {
                FabricHealerManager.RepairLogger.LogError($"Error getting node {nodeName}:{Environment.NewLine}{e}");
                return null;
            }
        }

        /// <summary>
        /// Repair task scheduling for jobs that are executed by FabriHealer.
        /// </summary>
        /// <param name="repairData">TelemetryData instance containing repair and state information.</param>
        /// <param name="cancellationToken">CancellationToken instance. This should generally be the SF runtime cancellation token (RunAsync).</param>
        /// <returns></returns>
        public static async Task<RepairTask> ScheduleFabricHealerRepairTaskAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            try
            {
                if (FabricHealerManager.InstanceCount is (-1) or > 1)
                {
                    await FabricHealerManager.RandomWaitAsync(cancellationToken);
                }

                if (await RepairTaskEngine.HasActiveStopFHRepairJob(cancellationToken))
                {
                    return null;
                }

                if (repairData.RepairPolicy == null)
                {
                    return null;
                }

                // FH instance and node checks when multiple instance of FH are running in a cluster.
                // FH can only restart system service processes on the machine where it is running. If it the repair is to restart a Fabric node, 
                // then the FH instance should not take ownership of the repair if it is running the target Fabric node.
                if (FabricHealerManager.InstanceCount is (-1) or > 1)
                {
                    // Block attempts to schedule Fabric node or system service restart repairs if one is already executing in the cluster.
                    var fhRepairTasks = await RepairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairConstants.FHTaskIdPrefix, cancellationToken);

                    if (fhRepairTasks != null && fhRepairTasks.Count > 0)
                    {
                        foreach (var repair in fhRepairTasks)
                        {
                            RepairExecutorData execData = JsonSerializationUtility.TryDeserializeObject(repair.ExecutorData, out RepairExecutorData exData) ? exData : null;

                            if (execData?.RepairPolicy?.RepairAction != RepairActionType.RestartFabricNode
                                && execData?.RepairPolicy?.RepairAction != RepairActionType.RestartProcess)
                            {
                                continue;
                            }

                            string message = $"A Service Fabric System service repair ({repair.TaskId}) is already in progress in the cluster(state: {repair.State}). " +
                                             $"Will not attempt repair at this time.";

                            await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    $"ProcessApplicationHealth::System::{repair.TaskId}",
                                    message,
                                    CancellationToken.None,
                                    null);

                            return null;
                        }
                    }

                    // Take ownership of Restart Fabric node repair only if this FH instance is not running on the target node.
                    if (repairData.RepairPolicy.RepairAction == RepairActionType.RestartFabricNode 
                        && repairData.NodeName == FabricHealerManager.ServiceContext.NodeContext.NodeName)
                    {
                        return null;
                    }

                    // Take ownership of Restart system service process repair only if the process is running on the same node as this FH instance.
                    if (repairData.RepairPolicy.RepairAction == RepairActionType.RestartProcess
                        && repairData.NodeName != FabricHealerManager.ServiceContext.NodeContext.NodeName)
                    {
                        return null;
                    }
                }

                // Internal throttling to protect against bad rules (over-scheduling of repair tasks within a fixed time range). 
                if (await CheckRepairCountThrottle(repairData, cancellationToken))
                {
                    string message = $"Too many repairs of this type have been scheduled in the last 15 minutes: " +
                                     $"{repairData.RepairPolicy.RepairId}. Will not schedule another repair at this time.";

                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"InternalThrottling({repairData.RepairPolicy.RepairId})",
                            message,
                            CancellationToken.None,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return null;
                }

                // Has the repair already been scheduled?
                if (await RepairTaskEngine.IsRepairInProgressAsync(repairData, cancellationToken))
                {
                    return null;
                }

                // Don't attempt a node-level repair on a node where there is already an active node-level repair.
                if (await RepairTaskEngine.IsNodeRepairCurrentlyInFlightAsync(repairData, cancellationToken))
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
                    RepairPolicy = repairData.RepairPolicy
                };

                // Create custom FH repair task for target node.
                var repairTask = await FabricRepairTasks.CreateRepairTaskAsync(repairData, executorData, cancellationToken);
                return repairTask;
            }
            catch (Exception e) when (e is TaskCanceledException)
            {
                return null;
            }
        }

        public static async Task<bool> ExecuteFabricHealerRepairTaskAsync(RepairTask repairTask, TelemetryData repairData, CancellationToken cancellationToken)
        {
            if (repairTask == null || repairData == null || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            TimeSpan approvalTimeout = TimeSpan.FromMinutes(60);
            Stopwatch stopWatch = Stopwatch.StartNew();
            bool isApproved = false;
            bool success;
            
            try
            {
                if (await RepairTaskEngine.HasActiveStopFHRepairJob(cancellationToken))
                {
                    await FabricRepairTasks.CancelRepairTaskAsync(repairTask);
                    return false;
                }

                if (FabricHealerManager.InstanceCount is (-1) or > 1)
                {
                    await FabricHealerManager.RandomWaitAsync(cancellationToken);
                }

                RepairTaskList repairs =
                    await RepairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(
                            RepairConstants.FHTaskIdPrefix,
                            cancellationToken);

                if (repairs != null && repairs.All(repair => repair.TaskId != repairTask.TaskId))
                {
                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "ExecuteFabricHealerRepairTask::NoJob",
                            $"Failed to find scheduled repair task {repairTask.TaskId}.",
                            CancellationToken.None,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return false;
                }

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "ExecuteFabricHealerRepairTask::WaitingForApproval",
                        $"Waiting for RM to Approve repair task {repairTask.TaskId}.",
                        CancellationToken.None,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                while (approvalTimeout >= stopWatch.Elapsed && !cancellationToken.IsCancellationRequested)
                {
                    repairs =
                        await RepairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(
                                RepairConstants.FHTaskIdPrefix,
                                cancellationToken,
                                RepairConstants.FabricHealer);

                    // Was repair cancelled (or cancellation requested) by another FH instance for some reason? Could be due to FH going down or a new deployment or a bug (fix it...).
                    if (repairs != null && repairs.Any(repair => repair.TaskId == repairTask.TaskId
                                        && (repair.State == RepairTaskState.Completed && repair.ResultStatus == RepairTaskResult.Cancelled
                                            || repair.Flags == RepairTaskFlags.CancelRequested || repair.Flags == RepairTaskFlags.AbortRequested)))
                    {
                        await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                "ExecuteFabricHealerRepairTask",
                                $"Repair Task {repairTask.TaskId} was aborted or cancelled.",
                                CancellationToken.None,
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
                            "ExecuteFabricHealerRepairTask::Approved",
                            $"RM has Approved repair task {repairTask.TaskId}.",
                            CancellationToken.None,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);
                }
                else
                {
                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "ExecuteFabricHealerRepairTask::NotApproved",
                            $"RM did not Approve repair task {repairTask.TaskId}. Cancelling...",
                            CancellationToken.None,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    await FabricRepairTasks.CancelRepairTaskAsync(repairTask);
                    return false;
                }
                
                // Move to Executing state.
                _ = await FabricRepairTasks.SetFabricRepairJobStateAsync(
                            repairTask,
                            RepairTaskState.Executing,
                            RepairTaskResult.Pending,
                            cancellationToken);

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                         LogLevel.Info,
                         "ExecuteFabricHealerRepairTask::MovedExecuting",
                         $"Executing repair {repairTask.TaskId}.",
                         CancellationToken.None,
                         repairData,
                         FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                var repairAction = repairData.RepairPolicy.RepairAction;

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
                            if (!RepairExecutor.TryGetGuid(repairData.PartitionId, out Guid partitionId))
                            {
                                success = false;
                                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "ExecuteFabricHealerRepairTask::NoPartition",
                                        $"No partition specified.",
                                        CancellationToken.None,
                                        repairData,
                                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                                break;
                            }

                            // Need replica or instance details..
                            var repList = await FabricHealerManager.FabricClientSingleton.QueryManager.GetReplicaListAsync(
                                                partitionId,
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
                        if (!RepairExecutor.TryGetGuid(repairData.PartitionId, out Guid partitionId))
                        {
                            success = false;
                            await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    "ExecuteFabricHealerRepairTask::NoPartition",
                                    $"No partition specified.",
                                    CancellationToken.None,
                                    repairData,
                                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                            break;
                        }

                        var repList = await FabricHealerManager.FabricClientSingleton.QueryManager.GetReplicaListAsync(
                                                partitionId,
                                                repairData.ReplicaId,
                                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                cancellationToken);

                        if (repList.Count == 0)
                        {
                            success = false;
                            await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    "ExecuteFabricHealerRepairTask::NoReplica",
                                    $"Stateless Instance {repairData.ReplicaId} not found on partition " +
                                    $"{repairData.PartitionId}.",
                                    CancellationToken.None,
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
                        if (!RepairExecutor.TryGetGuid(repairData.PartitionId, out Guid partitionId))
                        {
                            success = false;
                            await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    "ExecuteFabricHealerRepairTask::NoPartition",
                                    $"No partition specified.",
                                    CancellationToken.None,
                                    repairData,
                                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                            break;
                        }

                        var repList = await FabricHealerManager.FabricClientSingleton.QueryManager.GetReplicaListAsync(
                                                partitionId,
                                                repairData.ReplicaId,
                                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                cancellationToken);

                        if (repList.Count == 0)
                        {
                            success = false;
                            await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    "ExecuteFabricHealerRepairTask::NoReplica",
                                    $"Stateful replica {repairData.ReplicaId} not found on partition {partitionId}.",
                                    CancellationToken.None,
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
                        success = await RestartFabricNodeAsync(repairData, cancellationToken);
                        break;
                    }

                    default:
                        return false;
                }
            
                // What was the target (a node, app, replica, etc..)?
                string repairTarget = null;

                switch (repairData.EntityType)
                {
                    // Try and handle the case where EntityType is not specified (facts from FHProxy, for example)
                    // or is explicitly set to Unknown for some reason.
                    case EntityType.Unknown:

                        if (!string.IsNullOrWhiteSpace(repairData.ServiceName))
                        {
                            repairData.EntityType = EntityType.Service;
                            goto case EntityType.Service;
                        }
                        else if (!string.IsNullOrWhiteSpace(repairData.ApplicationName))
                        {
                            repairData.EntityType = EntityType.Application;
                            goto case EntityType.Application;
                        }
                        else if (!string.IsNullOrWhiteSpace(repairData.NodeName))
                        {
                            repairData.EntityType = EntityType.Node;
                            goto case EntityType.Node;
                        }
                        else if (repairData.ReplicaId > 0)
                        {
                            repairData.EntityType = EntityType.Replica;
                            goto case EntityType.Replica;
                        }
                        else if (!string.IsNullOrWhiteSpace(repairData.ProcessName) || repairData.ProcessId > 0)
                        {
                            repairData.EntityType = EntityType.Process;
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

                        repairTarget = "Unknown target type";
                        break;
                }

                if (success)
                {
                    string target = repairData.EntityType.ToString();
                    TimeSpan probationDuration = repairData.RepairPolicy.MaxTimePostRepairHealthCheck;

                    // Check healthstate of repair target to see if the repair worked: The target has been healthy for the specified probation duration.
                    bool isHealthy = await IsRepairTargetHealthyAfterProbation(repairData, probationDuration, cancellationToken);

                    if (isHealthy)
                    {
                        await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    "ExecuteFabricHealerRepairTaskAsync",
                                    $"{repairData.RepairPolicy.RepairAction} repair for {repairTarget} has succeeded.",
                                    CancellationToken.None,
                                    repairData,
                                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);
                    }
                    else
                    {
                        await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    $"ExecuteFabricHealerRepairTaskAsync::{repairTask.TaskId}",
                                    $"{repairData.RepairPolicy.RepairAction} repair for {repairTarget} has failed. " +
                                    $"{repairTarget} is still in an unhealthy state after {probationDuration} of post-repair probation.",
                                    CancellationToken.None,
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
            }
            catch (Exception e) when (e is ArgumentException or FabricException or OperationCanceledException or TaskCanceledException)
            {
#if DEBUG
                FabricHealerManager.RepairLogger.LogWarning($"Handled ExecuteFabricHealerRepairTaskAsync Failure:{Environment.NewLine}{e}");
#endif
                // Executor failure. Cancel repair task.
                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "ExecuteFabricHealerRepairTask_ExecuteFailed",
                        $"Execution or post-repair validation failed for job " +
                        $"{repairTask.TaskId}: {e.Message} " +
                        $"{(e is FabricException fabEx ? fabEx.ErrorCode : string.Empty)}. " +
                        $"Cancelling repair task. Expected = {cancellationToken.IsCancellationRequested}.",
                        CancellationToken.None,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);
            }

            await FabricRepairTasks.CancelRepairTaskAsync(repairTask);
            return false;
        }

        private static async Task<bool> RestartFabricNodeAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            return await RepairExecutor.RestartFabricNodeAsync(repairData, cancellationToken);
        }

        // Support for GetHealthEventHistoryPredicateType, which enables time-scoping logic rules based on health events related to specific SF entities/targets.
        internal static int GetEntityHealthEventCountWithinTimeRange(TelemetryData repairData, TimeSpan timeWindow)
        {
            int count = 0;

            if (repairData == null || DetectedHealthEvents == null || !DetectedHealthEvents.Any())
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

            count = DetectedHealthEvents.Count(
                        evt => evt.Name == id
                            && evt.HealthState == repairData.HealthState
                            && evt.SourceId == repairData.Source
                            && evt.Property == repairData.Property
                            && DateTime.UtcNow.Subtract(evt.SourceUtcTimestamp) <= timeWindow);

            // Lifetime management of Health Events list data. Cache lifecycle is 8 hours. If FH process restarts, data is not preserved.
            if (DateTime.UtcNow.Subtract(LastHealthEventsListClearDateTime) >= MaxLifeTimeHealthEventsData)
            {
                DetectedHealthEvents.Clear();
                LastHealthEventsListClearDateTime = DateTime.UtcNow;
            }

            return count;
        }

        /// <summary>
        /// Returns the amount of time the target entity (application, service, node, etc) has been in the specified health state. This data is held in a
        /// TelemetryData instance.
        /// </summary>
        /// <param name="repairData">TelemetryData instance that holds the data used to make the determination.</param>
        /// <returns>TimeSpan representing how long the specified entity has been in the specified health state.</returns>
        internal static TimeSpan GetEntityCurrentHealthStateDuration(TelemetryData repairData)
        {
            string name = repairData.EntityType switch
            {
                EntityType.Application => repairData.ApplicationName,
                EntityType.Partition => repairData.PartitionId.ToString(),
                EntityType.Replica => repairData.ReplicaId.ToString(),
                EntityType.Service => repairData.ServiceName,
                EntityType.Disk or EntityType.Machine or EntityType.Node => repairData.NodeName,
                _ => throw new NotSupportedException(
                    $"GetEntityCurrentHealthStateDuration: Specified entity type - {repairData.EntityType} - is not supported for this operation."),
            };

            try
            {
                if (DetectedHealthEvents == null || !DetectedHealthEvents.Any(d => d.Name == name))
                {
                    return TimeSpan.Zero;
                }

                var orderedEvents = DetectedHealthEvents.Where(
                        evt => evt.Name == name
                            && evt.HealthState == repairData.HealthState
                            && evt.SourceId == repairData.Source
                            && evt.Property == repairData.Property)
                        .OrderByDescending(o => o.SourceUtcTimestamp).ToList();

                // Lifetime management of volatile (in-memory) Health Events data. DetectedHealthEvents cache lifespan is 8 hours.
                // If the FH process restarts, data is not preserved.
                if (DateTime.UtcNow.Subtract(LastHealthEventsListClearDateTime) >= MaxLifeTimeHealthEventsData)
                {
                    DetectedHealthEvents.Clear();
                    LastHealthEventsListClearDateTime = DateTime.UtcNow;
                }

                if (!orderedEvents.Any()) 
                {
                    return TimeSpan.Zero;
                }

                /* Error/Warning state transitions - up/down Error/Warning state for node or multiple same error/warning events,
                   e.g., from a watchdog that runs periodically and produces the same Error/Warning event each time it runs
                   or some entity cycles between Error/Warning->Ok. */

                // Errors
                if (orderedEvents.First().LastErrorTransitionAt != DateTime.MinValue)
                {
                    return DateTime.UtcNow.Subtract(orderedEvents.First().LastErrorTransitionAt);
                }
                
                // Warnings
                if (orderedEvents.First().LastWarningTransitionAt != DateTime.MinValue)
                {
                    return DateTime.UtcNow.Subtract(orderedEvents.First().LastWarningTransitionAt);
                }

                // Catch-all (We shouldn't ever get here, but just in case)
                return DateTime.UtcNow.Subtract(orderedEvents.First().SourceUtcTimestamp);
            }
            catch (Exception e) when (e is ArgumentException or FabricException or InvalidOperationException or TaskCanceledException or TimeoutException)
            {
                string message = $"Unable to get {repairData.HealthState} health state duration for {repairData.EntityType}: {e.Message}";
                FabricHealerManager.RepairLogger.LogWarning(message);
            }

            return TimeSpan.Zero;
        }

        /// <summary>
        /// This function checks to see if the target of a repair has been healthy for the specified probation duration after a repair completes. 
        /// </summary>
        /// <param name="repairData">repairData instance.</param>
        /// <param name="probationPeriod">Amount of time to wait in health state probation.</param>
        /// <param name="token">CancellationToken instance.</param>
        /// <returns>Boolean representing whether the repair target is healthy after a completed repair operation within specified probationary duration.</returns>
        private static async Task<bool> IsRepairTargetHealthyAfterProbation(TelemetryData repairData, TimeSpan probationPeriod, CancellationToken token)
        {
            if (repairData == null)
            {
                return false;
            }

            // This means the logic rule specification didn't include a probabtion period or it was set to 00:00:00 by user. So, we'll just return true.
            if (probationPeriod <= TimeSpan.Zero)
            {
                return true;
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Wait.
                while (stopwatch.Elapsed <= probationPeriod)
                {
                    if (token.IsCancellationRequested)
                    {
                        // When the specified token is canceled, it could be either that the user-specified max execution time has been reached or 
                        // the RunAsync token has been canceled by the SF runtime. Either way, time to go.
                        return false;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
            }
            catch (TaskCanceledException) // token canceled during Task.Delay.
            {
                return false;
            }

            stopwatch.Stop();

            // Ensure target is healthy after the specified wait duration.
            return await GetCurrentEntityHealthStateAsync(repairData, token) == HealthState.Ok;
        }

        /// <summary>
        /// Determines current health state for repair target entity in supplied repair configuration.
        /// </summary>
        /// <param name="repairData">repairData instance.</param>
        /// <param name="token">CancellationToken instance.</param>
        /// <returns>HealthState enum reflecting current health state of target entity.</returns>
        private static async Task<HealthState> GetCurrentEntityHealthStateAsync(TelemetryData repairData, CancellationToken token)
        {
            try
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

                        if (appHealth == null)
                        {
                            return HealthState.Unknown;
                        }

                        // System Service repairs (process restarts)
                        if (repairData.ApplicationName == RepairConstants.SystemAppName)
                        {
                            isTargetAppHealedOnTargetNode = appHealth.HealthEvents.Any(
                                h => JsonSerializationUtility.TryDeserializeObject(h.HealthInformation.Description, out TelemetryData desc)
                                  && desc.NodeName == repairData.NodeName
                                  && desc.ProcessName == repairData.ProcessName
                                  && h.HealthInformation.HealthState == HealthState.Ok);
                        }
                        else // Application repairs (code package restarts)
                        {
                            isTargetAppHealedOnTargetNode =
                                appHealth.HealthEvents.Any(
                                    h => JsonSerializationUtility.TryDeserializeObject(h.HealthInformation.Description, out TelemetryData desc)
                                      && desc.NodeName == repairData.NodeName
                                      && desc.ApplicationName == repairData.ApplicationName
                                      && h.HealthInformation.HealthState == HealthState.Ok);
                        }

                        return isTargetAppHealedOnTargetNode ? HealthState.Ok : appHealth.AggregatedHealthState;

                    case EntityType.Service:

                        var serviceHealth = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                    () => FabricHealerManager.FabricClientSingleton.HealthManager.GetServiceHealthAsync(
                                                            new Uri(repairData.ServiceName),
                                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                            token),
                                                    token);

                        if (serviceHealth == null)
                        {
                            return HealthState.Unknown;
                        }

                        bool isTargetServiceHealedOnTargetNode =
                                serviceHealth.HealthEvents.Any(
                                   h => JsonSerializationUtility.TryDeserializeObject(h.HealthInformation.Description, out TelemetryData desc)
                                     && desc.NodeName == repairData.NodeName
                                     && desc.ServiceName == repairData.ServiceName
                                     && h.HealthInformation.HealthState == HealthState.Ok);

                        return isTargetServiceHealedOnTargetNode ? HealthState.Ok : serviceHealth.AggregatedHealthState;

                    case EntityType.Node:
                    case EntityType.Machine:

                        var nodeHealth = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                () => FabricHealerManager.FabricClientSingleton.HealthManager.GetNodeHealthAsync(
                                                        repairData.NodeName,
                                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                        token),
                                                token);

                        if (nodeHealth == null)
                        {
                            return HealthState.Unknown;
                        }

                        return nodeHealth.AggregatedHealthState;

                    case EntityType.Disk:

                        var diskHealth = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                () => FabricHealerManager.FabricClientSingleton.HealthManager.GetNodeHealthAsync(
                                                        repairData.NodeName,
                                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                        token),
                                                token);

                        if (diskHealth == null)
                        {
                            return HealthState.Unknown;
                        }

                        bool isTargetTargetNodeHealthy =
                               diskHealth.HealthEvents.Any(
                                  h => JsonSerializationUtility.TryDeserializeObject(h.HealthInformation.Description, out TelemetryData desc)
                                    && desc.NodeName == repairData.NodeName
                                    && SupportedErrorCodes.GetCodeNameFromErrorCode(desc.Code) != null
                                    && (SupportedErrorCodes.GetCodeNameFromErrorCode(desc.Code).Contains("Disk")
                                        || SupportedErrorCodes.GetCodeNameFromErrorCode(desc.Code).Contains("Folder"))
                                    && h.HealthInformation.HealthState == HealthState.Ok);

                        return isTargetTargetNodeHealthy ? HealthState.Ok : diskHealth.AggregatedHealthState;

                    case EntityType.Replica:

                        if (!RepairExecutor.TryGetGuid(repairData.PartitionId, out Guid partitionId))
                        {
                            return HealthState.Unknown;
                        }

                        // Make sure the Partition where the restarted replica was located is now healthy.
                        var partitionHealth = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                        () => FabricHealerManager.FabricClientSingleton.HealthManager.GetPartitionHealthAsync(
                                                                    partitionId,
                                                                    FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                    token),
                                                        token);

                        if (partitionHealth == null)
                        {
                            return HealthState.Unknown;
                        }

                        return partitionHealth.AggregatedHealthState;

                    default:
                        return HealthState.Unknown;
                }
            }
            catch (Exception e) when (e is FabricException or OperationCanceledException or TaskCanceledException or TimeoutException)
            {
                return HealthState.Unknown;
            }
        }
    }
}
