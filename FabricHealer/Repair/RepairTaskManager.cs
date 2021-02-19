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
using Guan.Common;
using System.Data;

namespace FabricHealer.Repair
{
    public class RepairTaskManager : IRepairTasks
    {
        private readonly RepairTaskEngine repairTaskEngine;
        internal readonly RepairExecutor RepairExec;
        internal readonly StatelessServiceContext Context;
        internal readonly CancellationToken Token;
        internal readonly TelemetryUtilities TelemetryUtilities;
        public readonly FabricClient FabricClientInstance;

        private TimeSpan AsyncTimeout 
        { 
            get; 
        } = TimeSpan.FromSeconds(60);

        public static readonly TimeSpan MaxWaitTimeForInfraRepairTaskCompleted = TimeSpan.FromHours(2);

        public RepairTaskManager(
            FabricClient fabricClient,
            StatelessServiceContext context,
            CancellationToken token)
        {
            this.FabricClientInstance = fabricClient ?? throw new ArgumentException("FabricClient can't be null");
            this.Context = context;
            this.Token = token;
            this.RepairExec = new RepairExecutor(fabricClient, context, token);
            this.repairTaskEngine = new RepairTaskEngine(fabricClient);
            this.TelemetryUtilities = new TelemetryUtilities(fabricClient, context);
        }

        public async Task EnableServiceFabricNodeAsync(
            string nodeName,
            CancellationToken cancellationToken)
        {
            await ActivateServiceFabricNodeAsync(nodeName, cancellationToken).ConfigureAwait(true);
        }

        public async Task RemoveServiceFabricNodeStateAsync(
            string nodeName,
            CancellationToken cancellationToken)
        {
            // TODO...
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public async Task ActivateServiceFabricNodeAsync(
            string nodeName, 
            CancellationToken cancellationToken)
        {
            await FabricClientInstance.ClusterManager.ActivateNodeAsync(
                nodeName,
                AsyncTimeout,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> SafeRestartServiceFabricNodeAsync(
            string nodeName,
            RepairTask repairTask,
            CancellationToken cancellationToken)
        {
            FabricHealerManager.RepairLogger.LogInfo(
                $"Taking down Fabric node {nodeName}.");

            if (!await RepairExec.SafeRestartFabricNodeAsync(
                nodeName,
                repairTask,
                cancellationToken).ConfigureAwait(false))
            {
                await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "SafeRestartFabricNodeAsync",
                    $"Did not restart Fabric node {nodeName}",
                    cancellationToken).ConfigureAwait(false);

                return false;
            }

            await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "SafeRestartFabricNodeAsync",
                    $"Successfully restarted Fabric node {nodeName}",
                    cancellationToken).ConfigureAwait(false);

            return true;
        }

        public async Task StartRepairWorkflowAsync(
                    TelemetryData foHealthData,
                    List<string> repairRules,
                    CancellationToken cancellationToken)
        {
            Node node = null;

            if (foHealthData.NodeName != null)
            {
                node = await GetFabricNodeFromNodeNameAsync(
                    foHealthData.NodeName,
                    cancellationToken).ConfigureAwait(false);
            }

            if (node == null)
            {
                await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        $"RepairTaskManager.StartRepairWorkflowAsync",
                        $"Unable to attempt repair. Target node exists in cluster? {node == null}.",
                        cancellationToken).ConfigureAwait(false);
              
                return;
            }

            try
            {
                if (repairRules.Any(r => r.Contains(RepairConstants.RestartVM)))
                {
                    // Do not allow VM reboot to take place in one-node cluster.
                    var nodes = await FabricClientInstance.QueryManager.GetNodeListAsync(
                                        null,
                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                        cancellationToken).ConfigureAwait(false);

                    int nodeCount = nodes.Count;

                    if (nodeCount == 1)
                    {
                        await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                              LogLevel.Warning,
                              $"RepairTaskManager.StartRepairWorkflowAsync::OneNodeCluster",
                              $"Will not attempt VM-level repair in a one node cluster.",
                              cancellationToken).ConfigureAwait(false);

                        return;
                    }
                }
            }
            catch (Exception e) when (e is FabricException || e is OperationCanceledException || e is TimeoutException)
            {
                await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                      LogLevel.Warning,
                      $"RepairTaskManager.StartRepairWorkflowAsync::NodeCount",
                      $"Unable to determine node count. Will not attempt VM level repairs:{Environment.NewLine}{e}",
                      cancellationToken).ConfigureAwait(false);

                return;
            }

            foHealthData.NodeType = node.NodeType;

            try
            {
                _ = await InitializeGuanAndRunQuery(foHealthData, repairRules);
            }
            catch (GuanException ge)
            {
                await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                       LogLevel.Warning,
                       "StartRepairWorkflowAsync:GuanException",
                       $"Failed in Guan: {ge}",
                       cancellationToken).ConfigureAwait(false);

                return;
            }
        }

        public async Task<bool> InitializeGuanAndRunQuery(
            TelemetryData foHealthData, 
            List<string> repairRules,
            RepairExecutorData repairExecutorData = null)
        {
            // ----- Guan Processing Logic -----
            // Add predicate types to functor table. Note that all health information data from FO are automatically passed to all predicates.
            // This enables access to various health state values in any query. See Mitigate() in rules files, for examples.
            FunctorTable functorTable = new FunctorTable();

            // Add external helper predicates.
            functorTable.Add(CheckFolderSizePredicateType.Singleton(RepairConstants.CheckFolderSize, this, foHealthData));
            functorTable.Add(GetRepairHistoryPredicateType.Singleton(RepairConstants.GetRepairHistory, this, foHealthData));
            functorTable.Add(CheckInsideRunIntervalPredicateType.Singleton(RepairConstants.CheckInsideRunInterval, this, foHealthData));

            // Add external repair predicates.
            functorTable.Add(DeleteFilesPredicateType.Singleton(RepairConstants.DeleteFiles, this, foHealthData));
            functorTable.Add(RestartCodePackagePredicateType.Singleton(RepairConstants.RestartCodePackage, this, foHealthData));
            functorTable.Add(RestartFabricNodePredicateType.Singleton(RepairConstants.RestartFabricNode, this, repairExecutorData, this.repairTaskEngine, foHealthData));
            functorTable.Add(RestartFabricSystemProcessPredicateType.Singleton(RepairConstants.RestartFabricSystemProcess, this, foHealthData));
            functorTable.Add(RestartReplicaPredicateType.Singleton(RepairConstants.RestartReplica, this, foHealthData));
            functorTable.Add(RestartVMPredicateType.Singleton(RepairConstants.RestartVM, this, foHealthData));

            // Parse rules
            Module module = Module.Parse("Module", repairRules, functorTable);
            var queryDispatcher = new GuanQueryDispatcher(module);

            // Create guan query
            List<CompoundTerm> terms = new List<CompoundTerm>();
            CompoundTerm term = new CompoundTerm("Mitigate");

            /* Pass default arguments in query. */
            // The type of metric that led FO to generate the unhealthy evaluation for the entity (App, Node, VM, Replica, etc).
            foHealthData.Metric = FabricObserverErrorWarningCodes.GetMetricNameFromCode(foHealthData.Code);

            term.AddArgument(new Constant(foHealthData.ApplicationName), RepairConstants.AppName);
            term.AddArgument(new Constant(foHealthData.Code), RepairConstants.FOErrorCode);
            term.AddArgument(new Constant(foHealthData.Metric), RepairConstants.MetricName);
            term.AddArgument(new Constant(foHealthData.NodeName), RepairConstants.NodeName);
            term.AddArgument(new Constant(foHealthData.NodeType), RepairConstants.NodeType);
            term.AddArgument(new Constant(foHealthData.ServiceName), RepairConstants.ServiceName);
            term.AddArgument(new Constant(foHealthData.SystemServiceProcessName), RepairConstants.SystemServiceProcessName);
            term.AddArgument(new Constant(foHealthData.PartitionId), RepairConstants.PartitionId);
            term.AddArgument(new Constant(foHealthData.ReplicaId), RepairConstants.ReplicaOrInstanceId);

            // FO metric values can be doubles or ints. We don't care about doubles here. That level of precision 
            // is not important and by converting to long we won't break default (long) Guan numeric comparison..
            term.AddArgument(new Constant(Convert.ToInt64((double)foHealthData.Value)), RepairConstants.MetricValue);

            terms.Add(term);

            // Dispatch query
            return await queryDispatcher.RunQueryAsync(terms).ConfigureAwait(false);
        }

        // The repair will be executed by SF Infrastructure service, not FH. This is the case for all
        // VM-level repairs. IS will communicate with VMSS (for example) to guarantee safe repairs in MR-enabled
        // clusters.RM, as usual, will orchestrate the repair cycle.
        public async Task<bool> ExecuteRMInfrastructureRepairTask(
            RepairConfiguration repairConfiguration,
            CancellationToken cancellationToken)
        {
            var infraServices = await FabricRepairTasks.GetInfrastructureServiceInstancesAsync(
                                        FabricClientInstance,
                                        cancellationToken).ConfigureAwait(false);

            if (infraServices.Count() == 0)
            {
                await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                   LogLevel.Info,
                   "RepairTaskManager.ExecuteRMInfrastructureRepairTask",
                   "Infrastructure Service not found. Will not attemp VM repair.",
                   cancellationToken,
                   repairConfiguration).ConfigureAwait(false);

                return false;
            }

            string executorName = null;

            foreach (var service in infraServices)
            {
                if (!service.ServiceName.OriginalString.Contains(repairConfiguration.NodeType))
                {
                    continue;
                }

                executorName = service.ServiceName.OriginalString;

                await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                           LogLevel.Info,
                           "RepairTaskManager.ExecuteRMInfrastructureRepairTask",
                           $"IS RepairTask {RepairTaskEngine.HostVMReboot} " +
                           $"Executor set to {executorName}.",
                           cancellationToken,
                           repairConfiguration).ConfigureAwait(false);

                break;
            }

            if (executorName == null)
            {
                await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                           LogLevel.Info,
                           "RepairTaskManager.ExecuteRMInfrastructureRepairTask",
                           "Unable to determine InfrastructureService service instance." +
                           "Exiting RepairTaskManager.ScheduleFHRepairTaskAsync.",
                           cancellationToken,
                           repairConfiguration).ConfigureAwait(false);

                return false;
            }

            // Make sure there is not already a repair job executing reboot/reimage repair for target node.
            var isRepairAlreadyInProgress =
                await repairTaskEngine.IsFHRepairTaskRunningAsync(
                    executorName,
                    repairConfiguration,
                    cancellationToken).ConfigureAwait(true);

            if (isRepairAlreadyInProgress)
            {
                await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                           LogLevel.Info,
                           "RepairTaskManager.ExecuteRMInfrastructureRepairTask",
                           $"Virtual machine repair task for VM " +
                           $"{await RepairExec.GetMachineHostNameFromFabricNodeNameAsync(repairConfiguration.NodeName, cancellationToken)} is already in progress. " +
                           $"Will not schedule another VM repair at this time.",
                           cancellationToken,
                           repairConfiguration).ConfigureAwait(false);

                return false;
            }

            // Create repair task for target node.
            var repairTask = await FabricRepairTasks.ScheduleRepairTaskAsync(
                    repairConfiguration,
                    null,
                    executorName,
                    FabricClientInstance,
                    cancellationToken).ConfigureAwait(false);

            if (repairTask == null)
            {
                await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                           LogLevel.Info,
                           "RepairTaskManager.ExecuteRMInfrastructureRepairTask",
                           "Unable to create Repair Task.",
                           cancellationToken,
                           repairConfiguration).ConfigureAwait(false);

                return false;
            }

            await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                           LogLevel.Info,
                           "RepairTaskManager.ExecuteRMInfrastructureRepairTask",
                           $"Successfully created Repair Task {repairTask.TaskId}",
                           cancellationToken,
                           repairConfiguration).ConfigureAwait(false);

            var timer = Stopwatch.StartNew();

            // It can take a while to get from a VM reboot/reimage to a healthy Fabric node, so block here until repair completes.
            // Note that, by design, this will block any other FabricHealer-initiated repair from taking place in the cluster.
            // FabricHealer is designed to be very conservative with respect to node level repairs. 
            // It is a good idea to not change this default behavior.
            while (timer.Elapsed < MaxWaitTimeForInfraRepairTaskCompleted)
            {
                if (!await FabricRepairTasks.IsRepairTaskInDesiredStateAsync(
                        repairTask.TaskId,
                        this.FabricClientInstance,
                        executorName,
                        new List<RepairTaskState> { RepairTaskState.Completed }))
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

                    continue;
                }

                timer.Stop();
                break;
            }

            return true;
        }

        public async Task<bool> DeleteFilesAsyncAsync(
            RepairConfiguration repairConfiguration,
            CancellationToken cancellationToken)
        {
            return await RepairExec.DeleteFilesAsync(repairConfiguration, cancellationToken);
        }

        public async Task<bool> RestartReplicaAsync(
            RepairConfiguration repairConfiguration,
            CancellationToken cancellationToken)
        {
            var result = await RepairExec.RestartReplicaAsync(
                                repairConfiguration ?? throw new ArgumentException("configuration can't be null."),
                                cancellationToken).ConfigureAwait(false);

            return result != null;
        }

        public async Task<bool> RemoveReplicaAsync(
            RepairConfiguration repairConfiguration,
            CancellationToken cancellationToken)
        {
            var result = await RepairExec.RemoveReplicaAsync(
                                repairConfiguration ?? throw new ArgumentException("configuration can't be null."),
                                cancellationToken).ConfigureAwait(false);

            return result != null;
        }

        public async Task<bool> RestartDeployedCodePackageAsync(
                                    RepairConfiguration repairConfiguration,
                                    CancellationToken cancellationToken)
        {
            string actionMessage =
                $"Attempting to restart deployed code package for app {repairConfiguration?.AppName.OriginalString}, " +
                $"service manifest {repairConfiguration?.CodePackage?.ServiceManifestName}";

            await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.RestartCodePackageAsync",
                    actionMessage,
                    cancellationToken,
                    repairConfiguration).ConfigureAwait(false);

            var result = await RepairExec.RestartCodePackageAsync(
                                repairConfiguration.AppName,
                                repairConfiguration.PartitionId,
                                repairConfiguration.ReplicaOrInstanceId,
                                repairConfiguration.ServiceName,
                                cancellationToken).ConfigureAwait(true);
            if (result == null)
            {
                return false;
            }

            string statusSuccess =
                    "Successfully restarted " +
                    $"code package {result.CodePackageName} with Instance Id " +
                    $"{result.CodePackageInstanceId} " +
                    $"for application {repairConfiguration.AppName.OriginalString} on node " +
                    $"{repairConfiguration.NodeName}.";

            await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                LogLevel.Info,
                "RepairExecutor.RestartCodePackageAsync",
                statusSuccess,
                cancellationToken,
                repairConfiguration).ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// Restarts Service Fabric system service process.
        /// </summary>
        /// <param name="repairConfiguration">RepairConfiguration instance.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A Task containing a boolean value representing success or failure of the repair action.</returns>
        private async Task<bool> RestartSystemServiceProcessAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(repairConfiguration.SystemServiceProcessName))
            {
                return false;
            }

            // Can only kill processes on the same node where FH instance that took the job is running.
            if (repairConfiguration.NodeName != Context.NodeContext.NodeName)
            {
                return false;
            }

            string actionMessage =
               $"Attempting to restart Service Fabric system process {repairConfiguration.SystemServiceProcessName}";

            await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.RestartSystemServiceProcessAsync",
                    actionMessage,
                    cancellationToken,
                    repairConfiguration).ConfigureAwait(false);

            bool result = await RepairExec.RestartSystemServiceProcessAsync(repairConfiguration, cancellationToken).ConfigureAwait(true);

            if (!result)
            {
                return false;
            }

            string statusSuccess = $"Successfully restarted Service Fabric system service process {repairConfiguration.SystemServiceProcessName} on node {repairConfiguration.NodeName}.";

            await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                LogLevel.Info,
                "RepairExecutor.RestartSystemServiceProcessAsync",
                statusSuccess,
                cancellationToken,
                repairConfiguration).ConfigureAwait(false);

            return true;
        }

        public async Task<Node> GetFabricNodeFromNodeNameAsync(string nodeName, CancellationToken cancellationToken)
        {
            try
            {
                var nodes = await this.FabricClientInstance.QueryManager.GetNodeListAsync(
                               nodeName,
                               AsyncTimeout,
                               cancellationToken).ConfigureAwait(true);

                return nodes.Count > 0 ? nodes[0] : null;
            }
            catch (FabricException fe)
            {
                FabricHealerManager.RepairLogger.LogError(
                    $"Error getting node {nodeName}:{Environment.NewLine}{fe}");

                return null;
            }
        }

        public async Task<RepairTask> ScheduleFabricHealerRmRepairTaskAsync(
            RepairConfiguration repairConfiguration,
            CancellationToken cancellationToken)
        {
            var isThisRepairTaskAlreadyInProgress =
                await repairTaskEngine.IsFHRepairTaskRunningAsync(
                    RepairTaskEngine.FabricHealerExecutorName,
                    repairConfiguration, 
                    cancellationToken).ConfigureAwait(true);

            // For the cases where this repair is already in flight.
            if (isThisRepairTaskAlreadyInProgress)
            { 
                string message = 
                    $"Node {repairConfiguration.NodeName} already has a " +
                    $"{Enum.GetName(typeof(RepairActionType), repairConfiguration.RepairPolicy.RepairAction)} repair in progress for repair Id {repairConfiguration.RepairPolicy.RepairId}. " +
                    "Exiting RepairTaskManager.ScheduleFabricHealerRmRepairTaskAsync.";

                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "ScheduleRepairTask:RepairAlreadyInProgress",
                    message,
                    cancellationToken,
                    repairConfiguration).ConfigureAwait(false);
                 
                return null;
            }

            // Don't attempt a node level repair on a node where there is already an active node-level repair.
            var currentlyExecutingRepairs =
                await this.FabricClientInstance.RepairManager.GetRepairTaskListAsync(
                    RepairTaskEngine.FHTaskIdPrefix,
                    RepairTaskStateFilter.Active | RepairTaskStateFilter.Executing,
                    RepairTaskEngine.FabricHealerExecutorName,
                    FabricHealerManager.ConfigSettings.AsyncTimeout,
                    cancellationToken).ConfigureAwait(true);

            if (currentlyExecutingRepairs.Count > 0)
            {
                foreach (var repair in currentlyExecutingRepairs.Where(task => task.ExecutorData.Contains(repairConfiguration.NodeName)))
                {
                    if (!SerializationUtility.TryDeserialize(repair.ExecutorData, out RepairExecutorData repairExecutorData))
                    {
                        continue;
                    }

                    if (repairExecutorData.RepairAction == RepairActionType.RestartFabricNode
                        || repairExecutorData.RepairAction == RepairActionType.RestartVM)
                    {
                        string message =
                           $"Node {repairConfiguration.NodeName} already has a node-impactful repair in progress: " +
                           $"{Enum.GetName(typeof(RepairActionType), repairConfiguration.RepairPolicy.RepairAction)}: {repair.TaskId}" +
                           "Exiting RepairTaskManager.ScheduleFabricHealerRmRepairTaskAsync.";

                        await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "ScheduleRepairTask::NodeRepairAlreadyInProgress",
                            message,
                            cancellationToken,
                            repairConfiguration).ConfigureAwait(false);

                        return null;
                    }
                }
            }

            var executorData = new RepairExecutorData
            {
                CustomIdentificationData = repairConfiguration.RepairPolicy.RepairId,
                ExecutorTimeoutInMinutes = (int)MaxWaitTimeForInfraRepairTaskCompleted.TotalMinutes,
                RestartRequestedTime = DateTime.Now,
                RepairAction = repairConfiguration.RepairPolicy.RepairAction,
                RepairPolicy = repairConfiguration.RepairPolicy,
                FOErrorCode = repairConfiguration.FOHealthCode,
                FOMetricValue = repairConfiguration.FOHealthMetricValue,
                NodeName = repairConfiguration.NodeName,
                NodeType = repairConfiguration.NodeType,
            };

            // Create custom FH repair task for target node.
            var repairTask = await FabricRepairTasks.ScheduleRepairTaskAsync(
                    repairConfiguration,
                    executorData,
                    RepairTaskEngine.FabricHealerExecutorName,
                    FabricClientInstance,
                    cancellationToken).ConfigureAwait(false);

            return repairTask;
        }

        public async Task<bool> ExecuteFabricHealerRmRepairTaskAsync(
            RepairTask repairTask,
            RepairConfiguration repairConfiguration,
            CancellationToken cancellationToken)
        {
            // Execute the repair.
            TimeSpan timeout = TimeSpan.FromMinutes(30);
            Stopwatch stopWatch = Stopwatch.StartNew();
            RepairTaskList repairs;

            while (timeout >= stopWatch.Elapsed)
            {
                repairs =
                    await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(
                        RepairTaskEngine.FabricHealerExecutorName,
                        cancellationToken).ConfigureAwait(true);

                if (repairs == null || repairs.Count == 0)
                {
                    await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                        $"Failed to schedule repair {repairTask.TaskId}.",
                        cancellationToken).ConfigureAwait(false);

                    return false;
                }

                if (repairs.All(task => task.TaskId != repairTask.TaskId))
                {
                    await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                        $"Failed to find scheduled repair task {repairTask.TaskId}.",
                        Token).ConfigureAwait(false);

                    return false;
                }

                await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                           LogLevel.Info,
                           "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync_WaitingForApproval",
                           $"Waiting for RM to Approve repair task {repairTask.TaskId}.",
                           cancellationToken).ConfigureAwait(false);

                if (!repairs.Any(task => task.TaskId == repairTask.TaskId
                                         && task.State == RepairTaskState.Approved))
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);
                    
                    continue;
                }

                await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                       LogLevel.Info,
                       "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync_Approved",
                       $"RM has Approved repair task {repairTask.TaskId}.",
                       cancellationToken).ConfigureAwait(false);

                break;
            }

            stopWatch.Stop();
            stopWatch.Reset();

            await FabricRepairTasks.SetFabricRepairJobStateAsync(
                    repairTask,
                    RepairTaskState.Executing,
                    RepairTaskResult.Pending,
                    FabricClientInstance,
                    cancellationToken).ConfigureAwait(true);
            
            await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync_MovedExecuting",
                        $"Executing repair {repairTask.TaskId}.",
                        cancellationToken).ConfigureAwait(false);

            bool success;
            var repairAction = repairConfiguration.RepairPolicy.RepairAction;

            switch (repairAction)
            {
                case RepairActionType.DeleteFiles:
                    
                    success = await DeleteFilesAsyncAsync(
                                    repairConfiguration,
                                    cancellationToken).ConfigureAwait(true);
                    break;

                // Note: For SF app container services, RestartDeployedCodePackage API does not work.
                // Thus, using Restart/Remove(stateful/stateless)Replica API instead, which does restart container instances.
                case RepairActionType.RestartCodePackage:

                    if (string.IsNullOrEmpty(repairConfiguration.ContainerId))
                    {
                        success = await RestartDeployedCodePackageAsync(
                            repairConfiguration, 
                            cancellationToken).ConfigureAwait(true);
                    }
                    else
                    {
                        // Need replica or instance details..
                        var repList = await FabricClientInstance.QueryManager.GetReplicaListAsync(
                                                    repairConfiguration.PartitionId,
                                                    repairConfiguration.ReplicaOrInstanceId,
                                                    FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                    cancellationToken).ConfigureAwait(false);
                        if (repList.Count == 0)
                        {
                            success = false;
                            break;
                        }

                        var rep = repList[0];

                        // Restarting stateful replica will restart the container instance.
                        if (rep.ServiceKind == ServiceKind.Stateful)
                        {
                            success = await RestartReplicaAsync(
                                            repairConfiguration,
                                            cancellationToken).ConfigureAwait(true);
                        }
                        else
                        {
                            // For stateless intances, you need to remove the replica, which will
                            // restart the container instance.
                            success = await RemoveReplicaAsync(
                                            repairConfiguration,
                                            cancellationToken).ConfigureAwait(true);
                        }
                    }

                    break;

                case RepairActionType.RemoveReplica:

                    success = await RemoveReplicaAsync(
                                    repairConfiguration,
                                    cancellationToken).ConfigureAwait(true);
                    break;

                case RepairActionType.RestartProcess:

                    success = await RestartSystemServiceProcessAsync(
                                repairConfiguration,
                                cancellationToken).ConfigureAwait(true);

                    break;

                case RepairActionType.RestartReplica:

                    var replicaList = await FabricClientInstance.QueryManager.GetReplicaListAsync(
                            repairConfiguration.PartitionId,
                            repairConfiguration.ReplicaOrInstanceId,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken).ConfigureAwait(false);

                    if (replicaList.Count == 0)
                    {
                        success = false;
                        await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                           LogLevel.Info,
                           $"RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                           $"Replica or Instance {repairConfiguration.ReplicaOrInstanceId} not found on partition {repairConfiguration.PartitionId}.",
                           cancellationToken).ConfigureAwait(false);

                        break;
                    }

                    var replica = replicaList[0];

                    // Restart - stateful replica.
                    if (replica.ServiceKind == ServiceKind.Stateful)
                    {
                        success = await RestartReplicaAsync(
                                        repairConfiguration,
                                        cancellationToken).ConfigureAwait(true);
                    }
                    else
                    {
                        // For stateless replicas, you need to remove the replica. The runtime will create a new one
                        // and place it..
                        success = await RemoveReplicaAsync(
                                        repairConfiguration,
                                        cancellationToken).ConfigureAwait(true);
                    }
                    break;

                case RepairActionType.RestartFabricNode:

                    var executorData = repairTask.ExecutorData;

                    if (string.IsNullOrEmpty(executorData))
                    {
                        success = false;
                    }
                    else
                    {
                        success = await SafeRestartServiceFabricNodeAsync(
                                        repairConfiguration.NodeName,
                                        repairTask,
                                        cancellationToken).ConfigureAwait(true);
                    }

                    break;

                default:
                    return false;
            }

            if (success)
            {
                string target = Enum.GetName(
                    typeof(RepairTargetType),
                    repairConfiguration.RepairPolicy.TargetType);

                TimeSpan maxWaitForHealthStateOk = TimeSpan.FromMinutes(60);

                if ((repairConfiguration.RepairPolicy.TargetType == RepairTargetType.Application
                    && repairConfiguration.AppName.OriginalString != "fabric:/System")
                    || repairConfiguration.RepairPolicy.TargetType == RepairTargetType.Replica)
                {
                    maxWaitForHealthStateOk = TimeSpan.FromMinutes(5);
                }
                else if (repairConfiguration.RepairPolicy.TargetType == RepairTargetType.Application
                         && repairConfiguration.AppName.OriginalString == "fabric:/System")
                {
                    maxWaitForHealthStateOk = TimeSpan.FromMinutes(20);
                }

                // Check healthstate of repair target to see if the repair worked.
                if (await IsRepairTargetHealthyAfterCompletedRepair(
                    repairConfiguration,
                    maxWaitForHealthStateOk,
                    cancellationToken).ConfigureAwait(false))
                {
                    await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                        $"{target} Repair target {repairConfiguration.RepairPolicy.RepairId} successfully healed on node {repairConfiguration.NodeName}.",
                        cancellationToken).ConfigureAwait(false);

                    // Tell RM we are ready to move to Completed state
                    // as our custom code has completed its repair execution successfully. This function
                    // puts the repair task into a Restoring State with ResultStatus Succeeded.
                    _ = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                () =>
                                FabricRepairTasks.CompleteCustomActionRepairJobAsync(
                                            repairTask,
                                            this.FabricClientInstance,
                                            this.Context,
                                            cancellationToken),
                                        cancellationToken).ConfigureAwait(false);
                    return true;
                }

                await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                        $"{target} Repair target {repairConfiguration.RepairPolicy.RepairId} not successfully healed.",
                        cancellationToken).ConfigureAwait(false);

                // Did not solve the problem within specified time. Cancel repair task.
                //await FabricRepairTasks.CancelRepairTaskAsync(repairTask, this.FabricClientInstance).ConfigureAwait(false);
                _ = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                () =>
                                FabricRepairTasks.CompleteCustomActionRepairJobAsync(
                                            repairTask,
                                            this.FabricClientInstance,
                                            this.Context,
                                            cancellationToken),
                                        cancellationToken).ConfigureAwait(false);

                return false;
            }

            // Executor failure. Cancel repair task.
            await this.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync_ExecuteFailed",
                        $"Executor failed for repair {repairTask.TaskId}. See logs for details. Cancelling repair task.",
                        cancellationToken).ConfigureAwait(false);

            await FabricRepairTasks.CancelRepairTaskAsync(repairTask, this.FabricClientInstance).ConfigureAwait(false);

            return false;
        }

        /// <summary>
        /// This function checks to see if the target of a repair is healthy after the repair task completed. 
        /// This will signal the result via telemetry and as a health event.
        /// </summary>
        /// <param name="repairConfig">RepairConfiguration instance</param>
        /// <param name="maxTimeToWait">Amount of time to wait for cluster to settle.</param>
        /// <param name="token">CancellationToken instance</param>
        /// <returns>Boolean representing whether the repair target is healthy after a completed repair operation.</returns>
        public async Task<bool> IsRepairTargetHealthyAfterCompletedRepair(
            RepairConfiguration repairConfig, 
            TimeSpan maxTimeToWait,
            CancellationToken token)
        {
            if (repairConfig == null)
            {
                return false;
            }

            var stopwatch = Stopwatch.StartNew();

            while (maxTimeToWait >= stopwatch.Elapsed)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (await GetCurrentAggregatedHealthStateAsync(
                    repairConfig,
                    token).ConfigureAwait(false) == HealthState.Ok)
                {
                    stopwatch.Stop();
                    return true;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(true);
            }

            stopwatch.Stop();

            return false;
        }

        /// <summary>
        /// Determines aggregated health state for repair target in supplied repair configuration.
        /// </summary>
        /// <param name="repairConfig">RepairConfiguration instance.</param>
        /// <param name="token">CancellationToken instance.</param>
        /// <returns></returns>
        private async Task<HealthState> GetCurrentAggregatedHealthStateAsync(
            RepairConfiguration repairConfig,
            CancellationToken token)
        {
            switch (repairConfig.RepairPolicy.TargetType)
            {
                case RepairTargetType.Application:

                    var appHealth = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () => this.FabricClientInstance.HealthManager.GetApplicationHealthAsync(
                                                repairConfig.AppName,
                                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                token),
                                            token);

                    bool isTargetAppHealedOnTargetNode = false;

                    // System Service repairs (Restarts)
                    if (repairConfig.AppName.OriginalString == "fabric:/System")
                    {
                       isTargetAppHealedOnTargetNode = appHealth.HealthEvents.Any(
                           h => SerializationUtility.TryDeserialize(h.HealthInformation.Description, out TelemetryData foHealthData) 
                                   && foHealthData.NodeName == repairConfig.NodeName 
                                   && foHealthData.SystemServiceProcessName == repairConfig.SystemServiceProcessName
                                   && foHealthData.HealthState.ToLower() == "ok");
                    }
                    else // Application repairs.
                    {
                        isTargetAppHealedOnTargetNode = appHealth.HealthEvents.Any(
                            h => SerializationUtility.TryDeserialize(h.HealthInformation.Description, out TelemetryData foHealthData)
                                    && foHealthData.NodeName == repairConfig.NodeName 
                                    && foHealthData.ApplicationName == repairConfig.AppName.OriginalString
                                    && foHealthData.HealthState.ToLower() == "ok");
                    }

                    return isTargetAppHealedOnTargetNode ? HealthState.Ok : appHealth.AggregatedHealthState;

                case RepairTargetType.Node:
                case RepairTargetType.VirtualMachine:

                    var nodeHealth = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () => this.FabricClientInstance.HealthManager.GetNodeHealthAsync(
                                                repairConfig.NodeName,
                                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                token),
                                            token);

                    bool isTargetNodeHealed = nodeHealth.HealthEvents.Any(
                            h => SerializationUtility.TryDeserialize(h.HealthInformation.Description, out TelemetryData foHealthData)
                                    && foHealthData.NodeName == repairConfig.NodeName
                                    && foHealthData.HealthState.ToLower() == "ok");

                    return isTargetNodeHealed ? HealthState.Ok : nodeHealth.AggregatedHealthState;

                case RepairTargetType.Replica:

                    // Make sure the Partition where the restarted replica was located is now healthy.
                    var partitionHealth = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                             () => this.FabricClientInstance.HealthManager.GetPartitionHealthAsync(
                                                     repairConfig.PartitionId,
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
