// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric.Description;
using System.Fabric.Health;
using System.Fabric.Query;
using System.Fabric.Repair;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;

namespace FabricHealer.Repair
{
    public sealed class RepairTaskEngine
    {
        public const string FHTaskIdPrefix = "FH";
        public const string AzureTaskIdPrefix = "Azure";
        public const string FabricHealerExecutorName = "FabricHealer";
        public const string InfrastructureServiceName = "fabric:/System/InfrastructureService";

        /// <summary>
        /// Schedules a repair task where FabricHealer is the executor.
        /// </summary>
        /// <param name="executorData"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        public async Task<RepairTask> ScheduleFabricHealerRepairTask(RepairExecutorData executorData, CancellationToken token)
        {
            if (executorData == null || executorData.RepairData.NodeName == null)
            {
                return null;
            }

            var repairs = await GetFHRepairTasksCurrentlyProcessingAsync(FabricHealerExecutorName, token);

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
                Executor = FabricHealerExecutorName,
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
        public async Task<RepairTaskList> GetFHRepairTasksCurrentlyProcessingAsync(string executorName, CancellationToken cancellationToken)
        {
            var repairTasks = await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                                        FHTaskIdPrefix,
                                        RepairTaskStateFilter.Active |
                                        RepairTaskStateFilter.Approved |
                                        RepairTaskStateFilter.Executing,
                                        executorName,
                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                        cancellationToken);

            return repairTasks;
        }

        /// <summary>
        /// Schedules a repair task where SF's InfrastructureService (IS) is the executor.
        /// </summary>
        /// <param name="repairData"></param>
        /// <param name="executorName"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<RepairTask> ScheduleInfrastructureRepairTaskAsync(TelemetryData repairData, string executorName, CancellationToken cancellationToken)
        {
            // This constraint (MaxResults) is used just to make sure there is more 1 node in the cluster. We don't need a list of all nodes.
            var nodeQueryDesc = new NodeQueryDescription
            {
                MaxResults = 3,
            };

            NodeList nodes = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                    () => FabricHealerManager.FabricClientSingleton.QueryManager.GetNodePagedListAsync(
                                            nodeQueryDesc,
                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                            cancellationToken),
                                     cancellationToken);

            if (nodes?.Count == 1)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(repairData.RepairPolicy.InfrastructureRepairName))
            {
                return null;
            }

            string taskId = $"{FHTaskIdPrefix}/{repairData.RepairPolicy.InfrastructureRepairName}/{repairData.NodeType}/{repairData.NodeName}";

            bool isRepairInProgress =
                await IsRepairInProgressOrMaxRepairsReachedAsync($"{RepairConstants.InfrastructureServiceName}/{repairData.NodeType}", repairData, cancellationToken);

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
                Description = $"{repairData.RepairPolicy.RepairId}",
                Executor = executorName,
                PerformPreparingHealthCheck = doHealthChecks,
                PerformRestoringHealthCheck = doHealthChecks,
                State = RepairTaskState.Claimed,
            };

            return repairTask;
        }

        /// <summary>
        /// Determines if a repair task is already in flight or if the max number of concurrent repairs has been reached for the target using the information specified in repairData instance.
        /// </summary>
        /// <param name="executorName">Name of the repair executor.</param>
        /// <param name="repairData">TelemetryData instance.</param>
        /// <param name="token">CancellationToken.</param>
        /// <param name="maxConcurrentRepairs">Optional: Number of max concurrent repairs for the entity type specified in repairData. Default is 0 which means no concurrent repairs.</param>
        /// <returns></returns>
        public async Task<bool> IsRepairInProgressOrMaxRepairsReachedAsync(string executorName, TelemetryData repairData, CancellationToken token)
        {
            // All RepairTasks are prefixed with FH, regardless of repair target type (VM/Machine, Fabric node, system service process, code package, replica).
            // For VM-level repairs, RM will create a new task for IS that replaces FH executor data with IS job info.
            RepairTaskList repairTasksInProgress =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            FHTaskIdPrefix,
                            RepairTaskStateFilter.Active | RepairTaskStateFilter.Approved | RepairTaskStateFilter.Executing,
                            executorName,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            token);

            if (repairTasksInProgress == null || repairTasksInProgress.Count == 0)
            {
                return false;
            }

            // Throttling machine level repairs.
            if (executorName == $"{InfrastructureServiceName}/{repairData.NodeType}" &&
                repairData.RepairPolicy.MaxConcurrentRepairs > 0 &&
                repairTasksInProgress.Count(r => r.Executor == executorName) >= repairData.RepairPolicy.MaxConcurrentRepairs)
            {
                return true;
            }

            foreach (var repair in repairTasksInProgress)
            {
                // This check is to see if there are any FH-as-executor repairs in flight.
                if (executorName == FabricHealerExecutorName)
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
                else if (repair.Executor == $"{InfrastructureServiceName}/{repairData.NodeType}")
                {
                    // This would block rescheduling any VM level operation (reboot) that is already in flight.
                    // NOTE: For Infrastructure-level repairs (IS is executor), unique id is stored in the repair task's Description property.
                    if (repair.Description == repairData.RepairPolicy.RepairId)
                    {
                        return true;
                    }
                }
                else
                {
                    if (repair.Impact is NodeRepairImpactDescription impact)
                    {
                        if (impact.Kind == RepairImpactKind.Node && impact.ImpactedNodes.Any(n => n.NodeName == repairData.NodeName))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }
}