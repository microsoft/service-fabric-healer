// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Query;
using System.Fabric.Repair;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;

namespace FabricHealer.Repair
{
    public static class FabricRepairTasks
    {
        public static async Task<bool> IsRepairTaskInDesiredStateAsync(
                                        string taskId,
                                        IList<RepairTaskState> desiredStates,
                                        CancellationToken cancellationToken)
        {
            IList<RepairTask> repairTaskList = 
                await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                        taskId,
                        RepairTaskStateFilter.All,
                        null,
                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                        cancellationToken);

            return desiredStates.Any(desiredState => repairTaskList.Count(rt => rt.State == desiredState) > 0);
        }

        /// <summary>
        /// Cancels a repair task based on its current state.
        /// </summary>
        /// <param name="repairTask"><see cref="RepairTask"/> to be cancelled</param>
        /// <returns></returns>
        /// <exception cref="FabricException">Throws FabricException if it can't complete the task.</exception>
        public static async Task CancelRepairTaskAsync(RepairTask repairTask)
        {
            switch (repairTask.State)
            {
                case RepairTaskState.Restoring:
                case RepairTaskState.Completed:
                    break;

                case RepairTaskState.Created:
                case RepairTaskState.Claimed:
                case RepairTaskState.Preparing:
                
                    try
                    {
                        _ = await FabricHealerManager.FabricClientSingleton.RepairManager.CancelRepairTaskAsync(repairTask.TaskId, repairTask.Version, true);
                    }
                    catch (FabricException)
                    {
                        throw;
                    }
                    catch (InvalidOperationException)
                    {
                        // RM throws IOE if state is already Completed or in some state that is not supported for state transition. Ignore.
                    }
                    break;
                
                case RepairTaskState.Approved:
                case RepairTaskState.Executing:

                    repairTask.State = RepairTaskState.Restoring;
                    repairTask.ResultStatus = RepairTaskResult.Cancelled;

                    try
                    {
                        _ = await FabricHealerManager.FabricClientSingleton.RepairManager.UpdateRepairExecutionStateAsync(repairTask);
                    }
                    catch (FabricException)
                    {
                        throw;
                    }
                    catch (InvalidOperationException)
                    {
                        //..
                    }
                    break;

                case RepairTaskState.Invalid:
                    break;
            }
        }

        public static async Task<bool> CompleteCustomActionRepairJobAsync(RepairTask repairTask, CancellationToken token)
        {
            try
            {
                if (repairTask.ResultStatus == RepairTaskResult.Succeeded
                    || repairTask.State == RepairTaskState.Completed
                    || repairTask.State == RepairTaskState.Restoring)
                {
                    return true;
                }

                repairTask.State = RepairTaskState.Restoring;
                repairTask.ResultStatus = RepairTaskResult.Succeeded;

                _ =  await FabricHealerManager.FabricClientSingleton.RepairManager.UpdateRepairExecutionStateAsync(
                                                            repairTask,
                                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                            token);
            }
            catch (Exception e) when (e is FabricException || e is TaskCanceledException || e is OperationCanceledException || e is TimeoutException)
            {
                return false;
            }
            catch (InvalidOperationException e)
            {
                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "FabricRepairTasks.CompleteCustomActionRepairJobAsync",
                        $"Failed to Complete Repair Job {repairTask.TaskId} due to invalid state transition.{Environment.NewLine}:{e.Message}",
                        token);

                return false;
            }
            catch (Exception e)
            {
                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "FabricRepairTasks.CompleteCustomActionRepairJobAsync",
                        $"Failed to Complete Repair Job {repairTask.TaskId} with unhandled exception:{Environment.NewLine}{e}",
                        token);

                throw;
            }

            return true;
        }

        public static async Task<RepairTask> CreateRepairTaskAsync(
                                                TelemetryData repairData,
                                                RepairExecutorData executorData,
                                                CancellationToken token)
        {
            RepairActionType repairAction = repairData.RepairPolicy.RepairAction;
            RepairTask repairTask;
            bool isRepairInProgress = await RepairTaskEngine.IsRepairInProgressAsync(repairData, token);

            if (isRepairInProgress)
            {
                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"CreateRepair::{repairData.RepairPolicy.RepairId}",
                        $"Repair {repairData.RepairPolicy.RepairId} has already been created.",
                        token,
                        null,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return null;
            }

            switch (repairAction)
            {
                // IS
                case RepairActionType.Infra:

                    repairTask = await RepairTaskEngine.CreateInfrastructureRepairTaskAsync(repairData, token);
                    break;
                
                // FH
                case RepairActionType.DeleteFiles:
                case RepairActionType.RestartCodePackage:
                case RepairActionType.RestartFabricNode:
                case RepairActionType.RestartProcess:
                case RepairActionType.RestartReplica:

                    repairTask = await RepairTaskEngine.CreateFabricHealerRepairTask(executorData, token);
                    break;

                default:

                    FabricHealerManager.RepairLogger.LogWarning("Unknown or Unsupported FabricRepairAction specified.");
                    return null;
            }

            bool success = await CreateRepairTaskAsync(
                                    repairTask,
                                    repairData,
                                    token);

            return success ? repairTask : null;
        }

        private static async Task<bool> CreateRepairTaskAsync(
                                            RepairTask repairTask,
                                            TelemetryData repairData,
                                            CancellationToken token)
        {
            if (repairTask == null)
            {
                return false;
            }

            try
            {
                var isRepairAlreadyInProgress =
                    await RepairTaskEngine.IsRepairInProgressAsync(repairData, token);

                if (!isRepairAlreadyInProgress)
                {
                    _ = await FabricHealerManager.FabricClientSingleton.RepairManager.CreateRepairTaskAsync(
                                repairTask,
                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                token);

                    return true;
                }
            }
            catch (ArgumentException ae)
            {
                string message = $"Unable to create repairtask:{Environment.NewLine}{ae}";

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "FabricRepairTasks::TryCreateRepairTaskAsync",
                        message,
                        token,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);
            }
            catch (FabricException fe)
            {
                string message = $"Unable to create repairtask:{Environment.NewLine}{fe}";

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "FabricRepairTasks::TryCreateRepairTaskAsync",
                        message,
                        token,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);
            }

            return false;
        }

        public static async Task<long> SetFabricRepairJobStateAsync(
                                          RepairTask repairTask,
                                          RepairTaskState repairState,
                                          RepairTaskResult repairResult,
                                          CancellationToken token)
        {
            repairTask.State = repairState;
            repairTask.ResultStatus = repairResult;

            return await FabricHealerManager.FabricClientSingleton.RepairManager.UpdateRepairExecutionStateAsync(
                            repairTask,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            token);
        }

        public static async Task<IEnumerable<Service>> GetInfrastructureServiceInstancesAsync(CancellationToken cancellationToken)
        {
            var allSystemServices =
                await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                               () => FabricHealerManager.FabricClientSingleton.QueryManager.GetServiceListAsync(
                                                        new Uri(RepairConstants.SystemAppName),
                                                        null,
                                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                        cancellationToken),

                                               cancellationToken);

            var infraInstances = 
                allSystemServices.Where(i => i.ServiceTypeName.Equals(RepairConstants.InfrastructureServiceType, StringComparison.InvariantCultureIgnoreCase));

            return infraInstances;
        }

        public static async Task<bool> IsLastCompletedFHRepairTaskWithinTimeRangeAsync(
                                         TimeSpan interval,
                                         TelemetryData repairData,
                                         CancellationToken cancellationToken)
        {
            var allRecentFHRepairTasksCompleted =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            repairData.RepairPolicy.RepairIdPrefix,
                            RepairTaskStateFilter.Completed,
                            null,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken);

            if (allRecentFHRepairTasksCompleted == null || allRecentFHRepairTasksCompleted.Count == 0)
            {
                return false;
            }

            var orderedRepairList = allRecentFHRepairTasksCompleted.OrderByDescending(o => o.CompletedTimestamp).ToList();

            if (repairData.RepairPolicy.RepairIdPrefix == RepairConstants.FHTaskIdPrefix)
            {
                var completedFHRepairs = orderedRepairList.Where(
                    r => r.ResultStatus == RepairTaskResult.Succeeded && r.ExecutorData.Contains(repairData.RepairPolicy.RepairId));

                foreach (var repair in completedFHRepairs)
                {
                    if (DateTime.UtcNow.Subtract(repair.CompletedTimestamp.Value) <= interval)
                    {
                        return true;
                    }
                }
            }
            else if (repairData.RepairPolicy.RepairIdPrefix == RepairConstants.InfraTaskIdPrefix)
            {
                var completedInfraRepairs = orderedRepairList.Where(r => r.ResultStatus == RepairTaskResult.Succeeded && r.Description == repairData.RepairPolicy.RepairId);
                
                foreach (var repair in completedInfraRepairs)
                {
                    if (DateTime.UtcNow.Subtract(repair.CompletedTimestamp.Value) <= interval)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static async Task<bool> IsLastScheduledRepairJobWithinTimeRangeAsync(
                                         TimeSpan interval,
                                         TelemetryData repairData,
                                         CancellationToken cancellationToken)
        {
            var allCurrentFHRepairTasks =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            repairData.RepairPolicy.RepairIdPrefix,
                            RepairTaskStateFilter.Active | RepairTaskStateFilter.Approved | RepairTaskStateFilter.Executing,
                            null,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken);

            if (allCurrentFHRepairTasks == null || allCurrentFHRepairTasks.Count == 0)
            {
                return false;
            }

            var orderedRepairList = allCurrentFHRepairTasks.OrderByDescending(o => o.CreatedTimestamp).ToList();

            foreach (var repair in orderedRepairList)
            {
                if (repair.CreatedTimestamp == null)
                {
                    continue;
                }

                if (repair.CreatedTimestamp.HasValue && DateTime.UtcNow.Subtract(repair.CreatedTimestamp.Value) <= interval)
                {
                    return true;
                }
            }
            
            return false;
        }

        /// <summary>
        /// Gets the number of completed repair tasks in the provided time range for FabricHealer-initiated repairs
        /// where either IS or FH is repair executor.
        /// </summary>
        /// <param name="timeWindow">TimeSpan representing the window of time to look for Completed FH/FH_Infra repair tasks.</param>
        /// <param name="repairData">TelemetryData instance that contains repair data.</param>
        /// <param name="cancellationToken">CancellationToken object.</param>
        /// <returns>the count as integer</returns>
        public static async Task<int> GetCompletedFHRepairCountWithinTimeRangeAsync(
                                         TimeSpan timeWindow,
                                         TelemetryData repairData,
                                         CancellationToken cancellationToken)
        {
            var allRecentFHRepairTasksCompleted =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            repairData.RepairPolicy.RepairIdPrefix,
                            RepairTaskStateFilter.Completed,
                            null,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken);

            if (allRecentFHRepairTasksCompleted?.Count == 0)
            {
                return 0;
            }

            var orderedRepairList = allRecentFHRepairTasksCompleted.OrderByDescending(o => o.CompletedTimestamp);
            int count = 0;

            foreach (RepairTask repair in orderedRepairList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Non-infra SF repair job scheduled and executed by FH.
                if (repairData.RepairPolicy.RepairIdPrefix == RepairConstants.FHTaskIdPrefix)
                {
                    // FH-owned RepairTask ExecutorData field will always hold a serialized instance of RepairExecutorData type.
                    if (!JsonSerializationUtility.TryDeserializeObject(repair.ExecutorData, out RepairExecutorData exData))
                    {
                        continue;
                    }

                    if (exData?.RepairPolicy == null)
                    {
                        continue;
                    }

                    if (repairData.RepairPolicy.RepairId != exData.RepairPolicy.RepairId)
                    {
                        continue;
                    }

                    if (DateTime.UtcNow.Subtract(repair.CompletedTimestamp.Value) <= timeWindow)
                    {
                        count++;
                    }
                }
                else // FH_Infra
                {
                    // This redundant check should always be true given the supplied repair ID prefix filter, but this check sets the NodeRepairTargetDescription variable
                    // and also protects against the improbable cases when Target is not of expected type. This is not a performance critical code path, but it must always 
                    // be correct to ensure count is accurate..
                    if (repair.Target is NodeRepairTargetDescription nodeRepairTargetDesc)
                    {
                        // Ensure both the repair Action and target node carried in the RepairData instance match the historical repair's information.
                        if (repairData.RepairPolicy.InfrastructureRepairName == repair.Action && nodeRepairTargetDesc.Nodes.Any(n => n == repairData.NodeName))
                        {
                            if (DateTime.UtcNow.Subtract(repair.CompletedTimestamp.Value) <= timeWindow)
                            {
                                count++;
                            }
                        }
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Gets the number of repairs that have been created within a specified time range.
        /// </summary>
        /// <param name="timeWindow">Time range to look for repairs.</param>
        /// <param name="repairData">TelemetryData instance.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns></returns>
        public static async Task<int> GetScheduledRepairCountWithinTimeRangeAsync(
                                         TimeSpan timeWindow,
                                         TelemetryData repairData,
                                         CancellationToken cancellationToken)
        {
            var allActiveFHRepairTasks =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            // Should this filter be applied? Does it matter if FH scheduled the repairs?
                            repairData.RepairPolicy.RepairIdPrefix,
                            RepairTaskStateFilter.Active | RepairTaskStateFilter.Approved | RepairTaskStateFilter.Executing,
                            null,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken);

            if (allActiveFHRepairTasks?.Count == 0)
            {
                return 0;
            }

            int count = 0;

            foreach (RepairTask repair in allActiveFHRepairTasks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (repair.CreatedTimestamp == null || !repair.CreatedTimestamp.HasValue)
                {
                    continue;
                }

                // Non-Machine repairs (FH is executor, custom repair ExecutorData supplied by FH.)
                if (repair.Executor == RepairConstants.FabricHealer)
                {
                    var fhExecutorData = JsonSerializationUtility.TryDeserializeObject(repair.ExecutorData, out RepairExecutorData exData) ? exData : null;

                    if (fhExecutorData == null || fhExecutorData.RepairPolicy == null)
                    {
                        continue;
                    }

                    if (repairData.RepairPolicy.RepairId != fhExecutorData.RepairPolicy.RepairId)
                    {
                        continue;
                    }

                    if (DateTime.UtcNow.Subtract(repair.CreatedTimestamp.Value) <= timeWindow)
                    {
                        count++;
                    }
                }
                // Machine/other source repairs.
                else if (DateTime.UtcNow.Subtract(repair.CreatedTimestamp.Value) <= timeWindow)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Determines if the target machine (node) is inside the specified post repair probation period.
        /// </summary>
        /// <param name="probationPeriod">Post-repair probation period.</param>
        /// <param name="nodeName">Name of the Service Fabric node for which a machine-level repair was conducted.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns></returns>
        internal static async Task<bool> IsMachineInPostRepairProbationAsync(
                                            TimeSpan probationPeriod,
                                            string nodeName,
                                            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(nodeName))
            {
                return false;
            }

            var allCompletedRepairTasks =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            null, // no prefix filter..
                            RepairTaskStateFilter.Completed,
                            null, // no executor name filter..
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken);

            if (allCompletedRepairTasks == null || allCompletedRepairTasks.Count == 0)
            {
                return false;
            }

            if (!allCompletedRepairTasks.Any(
                    r => r.Impact is NodeRepairImpactDescription nodeImpact
                      && nodeImpact.ImpactedNodes.Any(
                        n => n.NodeName == nodeName && (n.ImpactLevel == NodeImpactLevel.Restart || n.ImpactLevel == NodeImpactLevel.RemoveData))))
            {
                return false;
            }

            var orderedNodeRepairList =
                    allCompletedRepairTasks
                        .Where(r => r.Impact is NodeRepairImpactDescription nodeImpact
                                 && nodeImpact.ImpactedNodes.Any(
                                   n => n.NodeName == nodeName && (n.ImpactLevel == NodeImpactLevel.Restart || n.ImpactLevel == NodeImpactLevel.RemoveData)))
                        .OrderByDescending(o => o.CompletedTimestamp);

            foreach (RepairTask repair in orderedNodeRepairList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (repair.CompletedTimestamp.HasValue && DateTime.UtcNow.Subtract(repair.CompletedTimestamp.Value) < probationPeriod)
                {
                    return true;
                }
            }

            return false;
        }
    }
}