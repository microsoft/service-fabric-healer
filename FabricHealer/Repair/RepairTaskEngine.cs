// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Repair;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Utilities;

namespace FabricHealer.Repair
{
    public sealed class RepairTaskEngine
    {
        private readonly FabricClient fabricClient;
        public const string HostVMReboot = "System.Reboot";
        public const string FHTaskIdPrefix = "FH";
        public const string AzureTaskIdPrefix = "Azure";
        public const string FabricHealerExecutorName = "FabricHealer";
        public const string InfrastructureServiceName = "fabric:/System/InfrastructureService";

        public RepairTaskEngine(FabricClient fabricClient)
        {
            this.fabricClient = fabricClient;
        }

        public static RepairTask CreateFabricHealerRmRepairTask(RepairExecutorData executorData)
        {
            if (executorData == null)
            {
                return null;
            }

            var impact = executorData.RepairPolicy.RepairAction switch
            {
                RepairActionType.RestartFabricNode => NodeImpactLevel.Restart,
                RepairActionType.RemoveFabricNodeState => NodeImpactLevel.RemoveData,
                _ => NodeImpactLevel.None
            };

            var nodeRepairImpact = new NodeRepairImpactDescription();
            var impactedNode = new NodeImpact(executorData.NodeName, impact);
            nodeRepairImpact.ImpactedNodes.Add(impactedNode);
            RepairActionType repairAction = executorData.RepairPolicy.RepairAction;
            string repair = Enum.GetName(typeof(RepairActionType), repairAction);
            string taskId = $"{FHTaskIdPrefix}/{Guid.NewGuid()}/{repair}/{executorData.NodeName}";
            bool doHealthChecks = impact != NodeImpactLevel.None;

            // Error health state on target SF entity can block RM from approving the job to repair it (which is the whole point of doing the job).
            // So, do not do health checks if customer configures FO to emit Error health reports.
            // In general, FO should *not* be configured to emit Error events. See FO documentation.
            if (FOErrorWarningCodes.GetErrorWarningNameFromCode(executorData.FOErrorCode).Contains("Error"))
            {
                doHealthChecks = false;
            }

            var repairTask = new ClusterRepairTask(taskId, repair)
            {
                Target = new NodeRepairTargetDescription(executorData.NodeName),
                Impact = nodeRepairImpact,
                Description = $"FabricHealer executing repair {repair} on node {executorData.NodeName}",
                State = RepairTaskState.Preparing,
                Executor = FabricHealerExecutorName,
                ExecutorData = JsonSerializationUtility.TrySerialize(executorData, out string exData) ? exData : null,
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
            var repairTasks = await fabricClient.RepairManager.GetRepairTaskListAsync(
                                                                  FHTaskIdPrefix,
                                                                  RepairTaskStateFilter.Active |
                                                                  RepairTaskStateFilter.Approved |
                                                                  RepairTaskStateFilter.Executing,
                                                                  executorName,
                                                                  FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                  cancellationToken).ConfigureAwait(true);

            return repairTasks;
        }

        // This allows InfrastructureService to schedule and run reboot im concert with VMSS over MR.
        public async Task<RepairTask> CreateVmRebootTaskAsync(RepairConfiguration repairConfiguration, string executorName, CancellationToken cancellationToken)
        {
            // Do not allow this to take place in 1-node or 3-node clusters, like a dev cluster.
            // TODO: Block this from running in OneBox (dev machine) environment.
            var nodes = 
                await fabricClient.QueryManager.GetNodeListAsync(null, FabricHealerManager.ConfigSettings.AsyncTimeout, cancellationToken).ConfigureAwait(true);
            int nodeCount = nodes.Count;

            if (nodeCount <= 3)
            {
                return null;
            }

            string taskId = $"{FHTaskIdPrefix}/{HostVMReboot}/{(uint)repairConfiguration.NodeName.GetHashCode()}/{repairConfiguration.NodeType}";
            bool doHealthChecks = !FOErrorWarningCodes.GetErrorWarningNameFromCode(repairConfiguration.FOErrorCode).Contains("Error");

            // Error health state on target SF entity can block RM from approving the job to repair it (which is the whole point of doing the job).
            // So, do not do health checks if customer configures FO to emit Error health reports.
            // In general, FO should *not* be configured to emit Error events. See FO documentation.

            var repairTask = new ClusterRepairTask(taskId, HostVMReboot)
            {
                Target = new NodeRepairTargetDescription(repairConfiguration.NodeName),
                Description = $"{repairConfiguration.RepairPolicy.RepairId}",
                Executor = executorName,
                PerformPreparingHealthCheck = doHealthChecks,
                PerformRestoringHealthCheck = doHealthChecks,
                State = RepairTaskState.Claimed,
            };

            return repairTask;
        }

        public async Task<bool> IsFHRepairTaskRunningAsync(string executorName, RepairConfiguration repairConfig, CancellationToken token)
        {
            // All RepairTasks are prefixed with FH, regardless of repair target type (VM, fabric node, system service process, codepackage, replica).
            // For VM-level repair, RM will create a new task for IS that replaces FH executor data with IS job info, but the original FH repair task will
            // remain in an active state which will block any duplicate scheduling by another FH instance.
            var currentFHRepairTasksInProgress =
                            await fabricClient.RepairManager.GetRepairTaskListAsync(
                                                                FHTaskIdPrefix,
                                                                RepairTaskStateFilter.Active | RepairTaskStateFilter.Approved | RepairTaskStateFilter.Executing,
                                                                executorName,
                                                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                token).ConfigureAwait(true);

            if (currentFHRepairTasksInProgress == null || currentFHRepairTasksInProgress.Count == 0)
            {
                return false;
            }

            foreach (var repair in currentFHRepairTasksInProgress)
            {
                // This check is to see if there are any FH-as-executor repairs in flight.
                if (executorName == FabricHealerExecutorName)
                {
                    var exData = JsonSerializationUtility.TryDeserialize(repair.ExecutorData, out RepairExecutorData executorData) ? executorData : null;
                    
                    if (exData.RepairPolicy == null)
                    {
                        return false;
                    }

                    // The node repair check ensures that only one node-level repair can take place in a cluster (no concurrent node restarts), by default.
                    // FH is conservative, by design.
                    if (repairConfig.RepairPolicy.RepairId == executorData.RepairPolicy.RepairId ||
                        executorData.RepairPolicy.RepairAction == RepairActionType.RestartFabricNode)
                    {
                        return true;
                    }
                }
                else if (repair.Executor == $"{InfrastructureServiceName}/{repairConfig.NodeType}")
                {
                    // This would block scheduling any VM level operation (reboot) already in flight.
                    // For IS repairs, unique id is stored in the repair task's Description property.
                    if (repair.Description == repairConfig.RepairPolicy.RepairId)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
