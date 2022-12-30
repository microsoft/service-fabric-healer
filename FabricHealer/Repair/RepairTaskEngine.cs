// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Health;
using System.Fabric.Repair;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.TelemetryLib;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;

namespace FabricHealer.Repair
{
    public sealed class RepairTaskEngine
    {
        public const string FHTaskIdPrefix = "FH";
        public const string InfraTaskIdPrefix = "FH_Infra";
        public const string AzureTaskIdPrefix = "Azure";

        /// <summary>
        /// Creates a repair task where FabricHealer is the executor.
        /// </summary>
        /// <param name="executorData"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        public async Task<RepairTask> CreateFabricHealerRepairTask(RepairExecutorData executorData, CancellationToken token)
        {
            if (executorData == null || executorData.RepairData.NodeName == null)
            {
                return null;
            }

            var repairs = await GetFHRepairTasksCurrentlyProcessingAsync(FHTaskIdPrefix, token);

            if (repairs?.Count > 0)
            {
                if (repairs.Any(r => r.ExecutorData.Contains(executorData.RepairData.RepairPolicy.RepairId)))
                {
                    return null;
                }
            }

            NodeImpactLevel impact = executorData.RepairData.RepairPolicy.RepairAction switch
            {
                RepairActionType.RestartFabricNode => NodeImpactLevel.Restart,
                RepairActionType.RemoveFabricNodeState => NodeImpactLevel.RemoveData,
                _ => NodeImpactLevel.None
            };

            var nodeRepairImpact = new NodeRepairImpactDescription();
            var impactedNode = new NodeImpact(executorData.RepairData.NodeName, impact);
            nodeRepairImpact.ImpactedNodes.Add(impactedNode);
            RepairActionType repairAction = executorData.RepairData.RepairPolicy.RepairAction;
            string repair = repairAction.ToString();
            string taskId = $"{FHTaskIdPrefix}/{Guid.NewGuid()}/{repair}/{executorData.RepairData.NodeName}";
            bool doHealthChecks = impact != NodeImpactLevel.None;

            // Health checks for app level repairs.
            if (executorData.RepairData.RepairPolicy.DoHealthChecks && 
                impact == NodeImpactLevel.None &&
                            (repairAction == RepairActionType.RestartCodePackage ||
                                repairAction == RepairActionType.RestartReplica ||
                                repairAction == RepairActionType.RemoveReplica))
            {
                doHealthChecks = true;
            }

            // Error health state on target SF entity can block RM from approving the job to repair it (which is the whole point of doing the job).
            // So, do not do health checks if customer configures FO to emit Error health level reports.
            if (executorData.RepairData.HealthState == HealthState.Error)
            {
                doHealthChecks = false;
            }

            var repairTask = new ClusterRepairTask(taskId, repair)
            {
                Target = new NodeRepairTargetDescription(executorData.RepairData.NodeName),
                Impact = nodeRepairImpact,
                Description = $"FabricHealer executing repair {repair} on node {executorData.RepairData.NodeName}",
                State = RepairTaskState.Preparing,
                Executor = RepairConstants.FabricHealer,
                ExecutorData = JsonSerializationUtility.TrySerializeObject(executorData, out string exData) ? exData : null,
                PerformPreparingHealthCheck = doHealthChecks,
                PerformRestoringHealthCheck = doHealthChecks,
            };

            return repairTask;
        }

        /// <summary>
        /// This function returns the list of currently processing FH repair tasks.
        /// </summary>
        /// <returns>List of repair tasks in Preparing, Approved, Executing or Restoring state</returns>
        public async Task<RepairTaskList> GetFHRepairTasksCurrentlyProcessingAsync(string taskIdPrefix, CancellationToken cancellationToken)
        {
            var repairTasks = await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                                        taskIdPrefix,
                                        RepairTaskStateFilter.Active |
                                        RepairTaskStateFilter.Approved |
                                        RepairTaskStateFilter.Executing,
                                        null,
                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                        cancellationToken);

            return repairTasks;
        }

        /// <summary>
        /// Creates a repair task where SF's InfrastructureService (IS) is the executor.
        /// </summary>
        /// <param name="repairData"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<RepairTask> CreateInfrastructureRepairTaskAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            if (await FabricHealerManager.IsOneNodeClusterAsync())
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(repairData.RepairPolicy.InfrastructureRepairName))
            {
                return null;
            }

            string taskId = $"{InfraTaskIdPrefix}/{Guid.NewGuid()}/{repairData.NodeName}";
            bool isRepairInProgress = await IsRepairInProgressAsync(InfraTaskIdPrefix, repairData, cancellationToken);

            if (isRepairInProgress)
            {
                return null;
            }

            bool doHealthChecks = repairData.HealthState != HealthState.Error;

            // Error health state on target SF entity can block RM from approving the job to repair it (which is the whole point of doing the job).
            // So, do not do health checks if customer configures FO to emit Error health reports.
            // In general, FO should *not* be configured to emit Error events. See FO documentation.

            var repairTask = new ClusterRepairTask(taskId, repairData.RepairPolicy.InfrastructureRepairName)
            {
                Target = new NodeRepairTargetDescription(repairData.NodeName),
                Description = $"{repairData.RepairPolicy}",
                PerformPreparingHealthCheck = doHealthChecks,
                PerformRestoringHealthCheck = doHealthChecks,
                State = RepairTaskState.Created,
            };

            return repairTask;
        }

        /// <summary>
        /// Determines if a repair task is already in flight or if the max number of concurrent repairs has been reached for the target using the information specified in repairData instance.
        /// </summary>
        /// <param name="taskIdPrefix">The Task ID prefix.</param>
        /// <param name="repairData">TelemetryData instance.</param>
        /// <param name="token">CancellationToken.</param>
        /// <returns></returns>
        public async Task<bool> IsRepairInProgressAsync(string taskIdPrefix, TelemetryData repairData, CancellationToken token)
        {
            // All RepairTasks are prefixed with FH, regardless of repair target type (VM/Machine, Fabric node, system service process, code package, replica).
            // For Machine-level repairs, RM will create a new task for IS that replaces FH executor data with IS job info.
            RepairTaskList repairTasksInProgress =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            taskIdPrefix,
                            RepairTaskStateFilter.Active | RepairTaskStateFilter.Approved | RepairTaskStateFilter.Executing,
                            null,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            token);

            if (repairTasksInProgress == null || repairTasksInProgress.Count == 0)
            {
                return false;
            }

            foreach (var repair in repairTasksInProgress)
            {
                // FH is executor. Repair Task's ExecutorData field will always be a JSON-serialized instance of RepairExecutorData.
                if (taskIdPrefix == FHTaskIdPrefix)
                {
                    if (!JsonSerializationUtility.TryDeserializeObject(repair.ExecutorData, out RepairExecutorData executorData))
                    {
                        continue;
                    }

                    if (executorData.RepairData.RepairPolicy == null)
                    {
                        return false;
                    }

                    // This check ensures that only one repair can be scheduled at a time for the same target.
                    if (repairData.RepairPolicy.RepairId == executorData.RepairData.RepairPolicy.RepairId)
                    {
                        return true;
                    }
                }
                // InfrastructureService is executor. The related Repair Task's Description field is always the custom FH Repair ID.
                else if (!string.IsNullOrWhiteSpace(repairData.RepairPolicy.InfrastructureRepairName) && repair.Description == repairData.RepairPolicy.RepairId)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Determines if a node-impactful repair has already been scheduled/claimed for a target node.
        /// </summary>
        /// <param name="repairData">TelemetryData instance.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns></returns>
        public async Task<bool> IsNodeLevelRepairCurrentlyInFlightAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            try
            {
                var currentlyExecutingRepairs =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            null,
                            RepairTaskStateFilter.Active | RepairTaskStateFilter.Approved | RepairTaskStateFilter.Executing,
                            null,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken);

                if (currentlyExecutingRepairs.Count > 0)
                {
                    foreach (var repair in currentlyExecutingRepairs)
                    {
                        if (repair.Impact is NodeRepairImpactDescription impact)
                        {
                            if (!impact.ImpactedNodes.Any(
                                n => n.NodeName == repairData.NodeName && (n.ImpactLevel == NodeImpactLevel.Restart || n.ImpactLevel == NodeImpactLevel.RemoveData)))
                            {
                                continue;
                            }
                        }

                        return true;
                    }
                }
            }
            catch (Exception e) when (e is FabricException || e is TaskCanceledException || e is TimeoutException)
            {

            }

            return false;
        }

        public async Task<int> GetOutstandingFHRepairCount(string taskIdPrefix, CancellationToken token)
        {
            RepairTaskList repairTasksInProgress =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            taskIdPrefix,
                            RepairTaskStateFilter.Active | RepairTaskStateFilter.Approved | RepairTaskStateFilter.Executing,
                            null,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            token);

            if (repairTasksInProgress == null || repairTasksInProgress.Count == 0)
            {
                return 0;
            }

            return repairTasksInProgress.Count(r => r.TaskId.StartsWith(taskIdPrefix));
        }

        /// <summary>
        /// Get number of currently active repair jobs in the cluster.
        /// </summary>
        /// <param name="taskIdPrefix"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        public async Task<int> GetOutstandingMachineRepairCount(CancellationToken token)
        {
            int count = 0;

            try
            {
                var currentlyExecutingRepairs =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            null,
                            RepairTaskStateFilter.Active | RepairTaskStateFilter.Approved | RepairTaskStateFilter.Executing,
                            null,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            token);

                if (currentlyExecutingRepairs.Count > 0)
                {
                    foreach (var repair in currentlyExecutingRepairs)
                    {
                        if (repair.Impact is NodeRepairImpactDescription impact)
                        {
                            if (!impact.ImpactedNodes.Any(n => n.ImpactLevel == NodeImpactLevel.Restart || n.ImpactLevel == NodeImpactLevel.RemoveData))
                            {
                                continue;
                            }

                            count++;
                        }
                    }
                }
            }
            catch (Exception e) when (e is FabricException || e is TaskCanceledException || e is TimeoutException)
            {

            }

            return count;
        }
    }
}