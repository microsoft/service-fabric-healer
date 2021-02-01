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
        public const string HostVMReimage = "System.ReimageOS";
        public const string FHTaskIdPrefix = "FH";
        public const string AzureTaskIdPrefix = "Azure";
        public const string FabricHealerExecutorName = "FabricHealer";

        public bool IsFabricRepairManagerServiceDeployed
        {
            get; private set;
        }

        public RepairTaskEngine(
            FabricClient fabricClient)
        {
            this.fabricClient = fabricClient;
        }

        public RepairTask CreateFabricHealerRmRepairTask(
            RepairConfiguration repairConfiguration,
            RepairExecutorData executorData)
        {
            NodeImpactLevel impact = NodeImpactLevel.None;

            if (repairConfiguration.RepairPolicy.CurrentAction == RepairAction.RestartFabricNode)
            {
                impact = NodeImpactLevel.Restart;
            }
            else if (repairConfiguration.RepairPolicy.CurrentAction == RepairAction.RemoveFabricNodeState)
            {
                impact = NodeImpactLevel.RemoveData;
            }

            var nodeRepairImpact = new NodeRepairImpactDescription();
            var impactedNode = new NodeImpact(repairConfiguration.NodeName, impact);
            nodeRepairImpact.ImpactedNodes.Add(impactedNode);

            string taskId = $"{FHTaskIdPrefix}/{Enum.GetName(typeof(RepairAction), repairConfiguration.RepairPolicy.CurrentAction)}/{Guid.NewGuid()}/{repairConfiguration.NodeName}";
            
            var repairTask = new ClusterRepairTask(
                taskId,
                Enum.GetName(typeof(RepairAction), repairConfiguration.RepairPolicy.CurrentAction))
            {
                Target = new NodeRepairTargetDescription(repairConfiguration.NodeName),
                Impact = nodeRepairImpact,
                Description =
                    $"FabricHealer executing repair {Enum.GetName(typeof(RepairAction), executorData.RepairAction)} on node {repairConfiguration.NodeName}",
                State = RepairTaskState.Preparing,
                Executor = FabricHealerExecutorName,
                ExecutorData = SerializationUtility.TrySerialize(executorData, out string exData) ? exData : null,
                PerformPreparingHealthCheck = false,
                PerformRestoringHealthCheck = false,
            };

            return repairTask;
        }

        /// <summary>
        /// This function returns the list of currently processing FH repair tasks.
        /// </summary>
        /// <returns>List of repair tasks in Preparing, Approved, Executing or Restoring state</returns>
        public async Task<RepairTaskList> GetFHRepairTasksCurrentlyProcessingAsync(
            string executorName,
            CancellationToken cancellationToken)
        {
            var repairTasks = await this.fabricClient.RepairManager.GetRepairTaskListAsync(
                FHTaskIdPrefix,
                RepairTaskStateFilter.Active |
                RepairTaskStateFilter.Approved |
                RepairTaskStateFilter.Executing,
                executorName,
                FabricHealerManager.ConfigSettings.AsyncTimeout,
                cancellationToken).ConfigureAwait(false);

            return repairTasks;
        }

        // This allows InfrastructureService to schedule and run reboot
        public RepairTask CreateVmRebootTask(
            RepairConfiguration repairConfiguration,
            string executorName)
        {
            // Do not allow this to take place in one-node cluster.
            var nodes = this.fabricClient.QueryManager.GetNodeListAsync().GetAwaiter().GetResult();
            int nodeCount = nodes.Count;

            if (nodeCount == 1)
            {
                return null;
            }

            string taskId = $"{FHTaskIdPrefix}/{HostVMReboot}/{Guid.NewGuid()}/{repairConfiguration.NodeName}/{repairConfiguration.NodeType}";

            var repairTask = new ClusterRepairTask(taskId, HostVMReboot)
            {
                Target = new NodeRepairTargetDescription(repairConfiguration.NodeName),
                Description = $"{repairConfiguration.RepairPolicy.Id}",
                Executor = executorName,
                PerformPreparingHealthCheck = false,
                PerformRestoringHealthCheck = false,
                State = RepairTaskState.Claimed,
            };

            return repairTask;
        }

        public async Task<bool> IsFHRepairTaskRunningAsync(
            string executorName,
            RepairConfiguration repairConfig,
            CancellationToken token)
        {
            // All RepairTasks are prefixed with FH, regardless of repair target type (VM, fabric node, codepackage, replica...).
            // For VM-level repair, RM will create a new task for IS that replaces FH executor data with IS job info, but the original FH repair task will
            // remain in an active state which will block any duplicate scheduling by another FH instance.
            var currentFHRepairTasksInProgress =
                            await fabricClient.RepairManager.GetRepairTaskListAsync(
                                FHTaskIdPrefix,
                                RepairTaskStateFilter.Active | RepairTaskStateFilter.Approved | RepairTaskStateFilter.Executing,
                                executorName,
                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                token).ConfigureAwait(true);

            if (currentFHRepairTasksInProgress?.Count == 0)
            {
                return false;
            }

            foreach (var repair in currentFHRepairTasksInProgress)
            {
                var executorData =
                    SerializationUtility.TryDeserialize(repair.ExecutorData, out RepairExecutorData exData) ? exData : null;
                
                if (executorData == null)
                {
                    // This would block scheduling any VM level operation (reboot, reimage) already in flight. For IS repairs, state is stored in Description.
                    if (repair.Executor == $"fabric:/System/InfrastructureService/{repairConfig.NodeType}"
                        && repair.Description == repairConfig.RepairPolicy.Id)
                    {
                        return true;
                    }

                    continue;
                }

                if (repairConfig.RepairPolicy.Id == executorData.CustomIdentificationData
                    || executorData.RepairAction == RepairAction.RestartFabricNode)
                {
                    return true;
                } 
            }

            return false;
        }
    }
}
