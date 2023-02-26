// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Health;
using System.Fabric.Repair;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.TelemetryLib;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using Guan.Logic;

namespace FabricHealer.Repair
{
    public sealed class RepairTaskEngine
    {
        /// <summary>
        /// Supported repair action name substrings.
        /// </summary>
        public static readonly string[] NodeRepairActionSubstrings = new string[]
        {
            "azure.heal", "azure.host", "azure.job", "platform", "reboot", "reimage", "repave", "tenant", "triage"
        };

        /// <summary>
        /// Creates a repair task where FabricHealer is the executor.
        /// </summary>
        /// <param name="executorData"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        public static async Task<RepairTask> CreateFabricHealerRepairTask(RepairExecutorData executorData, CancellationToken token)
        {
            if (executorData == null)
            {
                return null;
            }

            var repairs = await GetFHRepairTasksCurrentlyProcessingAsync(RepairConstants.FHTaskIdPrefix, token);

            if (repairs?.Count > 0)
            {
                if (repairs.Any(r => r.ExecutorData.Contains(executorData.RepairPolicy.RepairId)))
                {
                    return null;
                }
            }

            NodeImpactLevel impact =
                executorData.RepairPolicy.NodeImpactLevel != NodeImpactLevel.Invalid ? executorData.RepairPolicy.NodeImpactLevel : NodeImpactLevel.None;
            NodeRepairImpactDescription nodeRepairImpact = new();
            NodeImpact impactedNode = new(executorData.RepairPolicy.NodeName, impact);
            nodeRepairImpact.ImpactedNodes.Add(impactedNode);
            RepairActionType repairAction = executorData.RepairPolicy.RepairAction;
            string repair = repairAction.ToString();
            string taskId = $"{RepairConstants.FHTaskIdPrefix}/{Guid.NewGuid()}/{repair}/{executorData.RepairPolicy.NodeName}";
            bool doHealthChecks = impact != NodeImpactLevel.None;

            // Health checks for app level repairs.
            if (executorData.RepairPolicy.DoHealthChecks && 
                impact == NodeImpactLevel.None &&
                            (repairAction == RepairActionType.RestartCodePackage ||
                                repairAction == RepairActionType.RestartReplica ||
                                repairAction == RepairActionType.RemoveReplica))
            {
                doHealthChecks = true;
            }

            // Error health state on target SF entity can block RM from approving the job to repair it (which is the whole point of doing the job).
            // So, do not do health checks if customer configures FO to emit Error health level reports.
            if (executorData.RepairPolicy.HealthState == HealthState.Error)
            {
                doHealthChecks = false;
            }

            string description = $"FabricHealer executing repair {repair} on node {executorData.RepairPolicy.NodeName}";

            if (impact == NodeImpactLevel.Restart || impact == NodeImpactLevel.RemoveData)
            {
                description = executorData.RepairPolicy.RepairId;
            }

            var repairTask = new ClusterRepairTask(taskId, repair)
            {
                Target = new NodeRepairTargetDescription(executorData.RepairPolicy.NodeName),
                Impact = nodeRepairImpact,
                Description = description,
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
        public static async Task<RepairTaskList> GetFHRepairTasksCurrentlyProcessingAsync(
                                                  string taskIdPrefix,
                                                  CancellationToken cancellationToken,
                                                  string executor = null)
        {
            var repairTasks = await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                                        taskIdPrefix,
                                        RepairTaskStateFilter.Active |
                                        RepairTaskStateFilter.Approved |
                                        RepairTaskStateFilter.Executing,
                                        executor,
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
        public static async Task<RepairTask> CreateInfrastructureRepairTaskAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(repairData.RepairPolicy.InfrastructureRepairName))
            {
                return null;
            }

            bool isRepairInProgress = await IsRepairInProgressAsync(repairData, cancellationToken);

            if (isRepairInProgress)
            {
                return null;
            }

            bool doHealthChecks = repairData.HealthState != HealthState.Error;
            string taskId = $"{RepairConstants.InfraTaskIdPrefix}/{Guid.NewGuid()}/{repairData.NodeName}";
            var repairTask = new ClusterRepairTask(taskId, repairData.RepairPolicy.InfrastructureRepairName)
            {
                Target = new NodeRepairTargetDescription(repairData.NodeName),
                Description = repairData.RepairPolicy.RepairId,
                PerformPreparingHealthCheck = doHealthChecks,
                PerformRestoringHealthCheck = doHealthChecks,
                State = RepairTaskState.Created
            };

            return repairTask;
        }

        /// <summary>
        /// Determines if a repair task is already in flight for the entity specified in the supplied TelemetryData instance.
        /// </summary>
        /// <param name="repairData">TelemetryData instance.</param>
        /// <param name="token">CancellationToken.</param>
        /// <returns>Returns true if a repair is already in progress. Otherwise, false.</returns>
        public static async Task<bool> IsRepairInProgressAsync(TelemetryData repairData, CancellationToken token)
        {
            if (repairData.RepairPolicy == null || string.IsNullOrWhiteSpace(repairData.RepairPolicy.RepairIdPrefix))
            {
                return false;
            }

            if (FabricHealerManager.InstanceCount == -1 || FabricHealerManager.InstanceCount > 1)
            {
                await FabricHealerManager.RandomWaitAsync();
            }

            RepairTaskList repairTasksInProgress =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            repairData.RepairPolicy.RepairIdPrefix,
                            RepairTaskStateFilter.Active,
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
                if (repairData.RepairPolicy.RepairIdPrefix == RepairConstants.FHTaskIdPrefix)
                {
                    if (!JsonSerializationUtility.TryDeserializeObject(repair.ExecutorData, out RepairExecutorData executorData))
                    {
                        continue;
                    }

                    if (executorData?.RepairPolicy == null)
                    {
                        return false;
                    }

                    // This check ensures that only one repair can be scheduled at a time for the same target.
                    if (repairData.RepairPolicy.RepairId.Equals(executorData.RepairPolicy.RepairId, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                // InfrastructureService is executor. The related Repair Task's Description field is always the custom (internal) FH Repair ID.
                else
                {
                    if (!string.IsNullOrWhiteSpace(repairData.RepairPolicy.InfrastructureRepairName) &&
                        repair.Description.Equals(repairData.RepairPolicy.RepairId, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    // Default is 0, which means unlimited.
                    if (repairData.RepairPolicy.MaxConcurrentRepairs > 0)
                    {
                        if (await GetAllOutstandingFHRepairsCountAsync(
                            RepairConstants.InfraTaskIdPrefix, token) >= repairData.RepairPolicy.MaxConcurrentRepairs)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Determines if a node-impactful repair has already been scheduled/claimed for a target node.
        /// </summary>
        /// <param name="repairData">TelemetryData instance.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Returns true if a repair job is currently in flight that has node-level impact. Otherwise, false.</returns>
        public static async Task<bool> IsNodeLevelRepairCurrentlyInFlightAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            try
            {
                if (FabricHealerManager.InstanceCount == -1 || FabricHealerManager.InstanceCount > 1)
                {
                    await FabricHealerManager.RandomWaitAsync();
                }

                RepairTaskList activeRepairs =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            null,
                            RepairTaskStateFilter.Active,
                            null,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken);
                
                if (activeRepairs.Count > 0)
                {
                    foreach (RepairTask repair in activeRepairs)
                    {
                        // FH does not execute machine level repairs.
                        if (repair.TaskId.StartsWith($"{RepairConstants.FHTaskIdPrefix}/") || repair.Executor == RepairConstants.FabricHealer)
                        {
                            continue;
                        }

                        // This would mean that the job has node-level Impact and its state is at least Approved.
                        if (repair.Impact is NodeRepairImpactDescription impact)
                        {
                            if (!impact.ImpactedNodes.Any(
                                n => n.NodeName == repairData.NodeName
                                  && (n.ImpactLevel == NodeImpactLevel.Restart ||
                                      n.ImpactLevel == NodeImpactLevel.RemoveData ||
                                      n.ImpactLevel == NodeImpactLevel.RemoveNode)))
                            {
                                continue;
                            }

                            return true;
                        }

                        // State == Created/Claimed if we get here (there is no Impact established yet).
                        if (repair.Target is NodeRepairTargetDescription target) 
                        {
                            if (!target.Nodes.Any(n => n == repairData.NodeName))
                            {
                                continue;
                            }

                            if ((!string.IsNullOrWhiteSpace(repair.Executor)
                                   && repair.Executor.Contains(RepairConstants.InfrastructureService, StringComparison.OrdinalIgnoreCase))
                                || MatchSubstring(NodeRepairActionSubstrings, repair.Action))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception e) when (e is ArgumentException || e is FabricException || e is TaskCanceledException || e is TimeoutException)
            {
#if DEBUG
                // This is not interesting. Means a one of FH's token source's was canceled in an expected way.
                if (e is not TaskCanceledException)
                {
                    FabricHealerManager.RepairLogger.LogWarning($"Handled Exception in IsNodeLevelRepairCurrentlyInFlightAsync:{Environment.NewLine}{e}");
                }
#endif
            }

            return false;
        }

        public static async Task<int> GetAllOutstandingFHRepairsCountAsync(string taskIdPrefix, CancellationToken token)
        {
            if (FabricHealerManager.InstanceCount == -1 || FabricHealerManager.InstanceCount > 1)
            {
                await FabricHealerManager.RandomWaitAsync();
            }

            if (taskIdPrefix == RepairConstants.InfraTaskIdPrefix) 
            {
                return await GetAllOutstandingNodeRepairsCountAsync(token);   
            }

            RepairTaskList repairTasksInProgress =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            taskIdPrefix,
                            RepairTaskStateFilter.Active,
                            null,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            token);

            if (repairTasksInProgress == null || repairTasksInProgress.Count == 0)
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(taskIdPrefix))
            {
                return repairTasksInProgress.Count;
            }

            return repairTasksInProgress.Count(r => r.TaskId.StartsWith(taskIdPrefix));
        }

        private static async Task<int> GetAllOutstandingNodeRepairsCountAsync(CancellationToken token)
        {
            RepairTaskList repairTasksInProgress =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            null,
                            RepairTaskStateFilter.Active,
                            null,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            token);
            int count = 0;

            if (repairTasksInProgress.Count > 0)
            {
                foreach (RepairTask repair in repairTasksInProgress)
                {
                    if (string.IsNullOrWhiteSpace(repair.TaskId) || repair.Target.Kind != RepairTargetKind.Node)
                    {
                        continue;
                    }

                    // FH does not execute machine level repairs.
                    if (repair.TaskId.StartsWith($"{RepairConstants.FHTaskIdPrefix}/") || repair.Executor == RepairConstants.FabricHealer)
                    {
                        continue;
                    }

                    // This would mean that the job has node-level impact and its state is at least Approved (Impact and ImpactLevel have been set).
                    if (repair.Impact is NodeRepairImpactDescription impact)
                    {
                        if (impact.ImpactedNodes.Any(
                                n => n.ImpactLevel == NodeImpactLevel.Restart ||
                                     n.ImpactLevel == NodeImpactLevel.RemoveData ||
                                     n.ImpactLevel == NodeImpactLevel.RemoveNode))
                        {
                            count++;
                        }
                    }
                    // Claimed/Created (no Impact has been established yet).
                    else if (repair.Target is NodeRepairTargetDescription)
                    {
                        if ((!string.IsNullOrWhiteSpace(repair.Executor)
                               && repair.Executor.Contains(RepairConstants.InfrastructureService, StringComparison.OrdinalIgnoreCase))
                            || MatchSubstring(NodeRepairActionSubstrings, repair.Action))
                        {
                            count++;
                        }
                    }
                }
            }

            return count;
        }

        internal static async Task<bool> CheckForActiveStopFHRepairJob(CancellationToken token)
        {
            RepairTaskList repairTasksInProgress =
                   await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                           null,
                           RepairTaskStateFilter.Active,
                           null,
                           FabricHealerManager.ConfigSettings.AsyncTimeout,
                           token);

            if (repairTasksInProgress.Count > 0)
            {
                foreach (RepairTask repair in repairTasksInProgress)
                {
                    // This means FH should stop scheduling/executing repairs.
                    if (repair.Action == RepairConstants.FabricHealerStopAction)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static async Task<bool> CheckForActiveStartFHRepairJob(CancellationToken token)
        {
            RepairTaskList repairTasksInProgress =
                   await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                           null,
                           RepairTaskStateFilter.Active,
                           null,
                           FabricHealerManager.ConfigSettings.AsyncTimeout,
                           token);

            if (repairTasksInProgress.Count > 0)
            {
                foreach (RepairTask repair in repairTasksInProgress)
                {
                    // This means FH should resume scheduling/executing repairs.
                    if (repair.Action == RepairConstants.FabricHealerStartAction)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool MatchSubstring(string[] substringArray, string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            for (int i = 0; i < substringArray.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(substringArray[i]) || !source.Contains(substringArray[i], StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        internal static async Task<bool> TryTraceCurrentlyExecutingRule(string predicate, TelemetryData repairData)
        {
            string ruleFileName = FabricHealerManager.CurrentlyExecutingLogicRulesFileName, rule = string.Empty;
            int lineNumber = 0;

            try
            {
                string ruleFilePath =
                    Path.Combine(
                        FabricHealerManager.ServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Path,
                        "LogicRules",
                        ruleFileName);

                if (!File.Exists(ruleFilePath))
                {
                    FabricHealerManager.RepairLogger.LogWarning($"TryTraceCurrentlyExecutingRule: Specified rule file path does not exist: {ruleFilePath}.");
                    return false;
                }

                string[] lines = File.ReadLines(ruleFilePath).ToArray();
                int length = lines.Length;
                predicate = predicate.Replace("'", "").Replace("\"", "").Replace(" ", "");

                for (int i = 0; i < length; i++)
                {
                    string line = lines[i].Replace("'", "").Replace("\"", "").Replace(" ", "");
                    
                    if (line.Contains("##") || string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (line.Contains(predicate, StringComparison.OrdinalIgnoreCase))
                    {
                        lineNumber = i;
                        line = lines[lineNumber];

                        if (line.StartsWith("Mitigate") && line.EndsWith("."))
                        {

                        }

                        // final (repair) predicate ends with a . in FH.
                        if (line.TrimEnd().EndsWith('.'))
                        {
                            rule = line.Replace('\t', ' ');

                            // Line is the whole rule.
                            if (line.Contains(":-"))
                            {
                                break;
                            }

                            for (int j = lineNumber - 1; j < length; j--)
                            {
                                if (lines[j].TrimEnd().EndsWith(','))
                                {
                                    rule = lines[j].Replace('\t', ' ').Trim() + ' ' + rule;
                                    lineNumber = j;

                                    if (lines[j].StartsWith("Mitigate"))
                                    {
                                        break;
                                    }
                                }
                            }
                        }

                        break;
                    }
                }

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"{ruleFileName}#{lineNumber}_{repairData.RepairPolicy.ProcessName ?? repairData.NodeName}",
                        $"Executing logic rule \'{rule}\'",
                        FabricHealerManager.Token);

                return true;
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is SystemException)
            {
                string message = $"TraceCurrentlyExecutingRule failure => Unable to read {ruleFileName}: {e.Message}";
                FabricHealerManager.RepairLogger.LogWarning(message);
                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"TraceCurrentlyExecutingRule::{ruleFileName}::Failure",
                        message,
                        FabricHealerManager.Token);
            }

            return false;
        }
    }
}