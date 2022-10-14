// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
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
    public static class FabricRepairTasks
    {
        public static async Task<bool> IsRepairTaskInDesiredStateAsync(
                                        string taskId,
                                        IList<RepairTaskState> desiredStates)
        {
            IList<RepairTask> repairTaskList = await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(taskId, RepairTaskStateFilter.All, null);
            return desiredStates.Any(desiredState => repairTaskList.Count(rt => rt.State == desiredState) > 0);
        }

        /// <summary>
        /// Cancels a repair task based on its current state.
        /// </summary>
        /// <param name="repairTask"><see cref="RepairTask"/> to be cancelled</param>
        /// <returns></returns>
        public static async Task CancelRepairTaskAsync(RepairTask repairTask)
        {
            await Task.Delay(1000);

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
                    catch (InvalidOperationException)
                    {
                        // RM throws IOE if state is already Completed or in some state that is not supported for transition. Ignore.
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
                    catch (FabricException fe)
                    {
                        // TOTHINK
                        if (fe.ErrorCode == FabricErrorCode.SequenceNumberCheckFailed)
                        {
                            // Not sure what to do here. This can randomly take place (timing).
                        }
                    }
                    break;

                case RepairTaskState.Invalid:

                    break;

                default:

                    throw new Exception($"Repair task {repairTask.TaskId} is in invalid state {repairTask.State}");
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
                        $"Failed to Complete Repair Job {repairTask.TaskId} due to invalid state transition.{Environment.NewLine}:{e}",
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
                                                string taskIdPrefix,
                                                CancellationToken token)
        {
            var repairTaskEngine = new RepairTaskEngine();
            RepairActionType repairAction = repairData.RepairPolicy.RepairAction;
            RepairTask repairTask;
            bool isRepairInProgress = await repairTaskEngine.IsRepairInProgressAsync(taskIdPrefix, repairData, token);

            if (isRepairInProgress)
            {
                return null;
            }

            switch (repairAction)
            {
                // IS
                case RepairActionType.Infra:

                    repairTask = await repairTaskEngine.CreateInfrastructureRepairTaskAsync(repairData, token);
                    break;
                
                // FH
                case RepairActionType.DeleteFiles:
                case RepairActionType.RestartCodePackage:
                case RepairActionType.RestartFabricNode:
                case RepairActionType.RestartProcess:
                case RepairActionType.RestartReplica:

                    repairTask = await repairTaskEngine.CreateFabricHealerRepairTask(executorData, token);
                    break;

                default:

                    FabricHealerManager.RepairLogger.LogWarning("Unknown or Unsupported FabricRepairAction specified.");
                    return null;
            }

            bool success = await RepairManagerCreateRepairTaskAsync(
                                    repairTask,
                                    repairData,
                                    repairTaskEngine,
                                    token);

            return success ? repairTask : null;
        }

        private static async Task<bool> RepairManagerCreateRepairTaskAsync(
                                            RepairTask repairTask,
                                            TelemetryData repairData,
                                            RepairTaskEngine repairTaskEngine,
                                            CancellationToken token)
        {
            if (repairTask == null)
            {
                return false;
            }

            try
            {
                var isRepairAlreadyInProgress =
                    await repairTaskEngine.IsRepairInProgressAsync(repairTask.Executor, repairData, token);

                if (!isRepairAlreadyInProgress)
                {
                    _ = await FabricHealerManager.FabricClientSingleton.RepairManager.CreateRepairTaskAsync(repairTask, FabricHealerManager.ConfigSettings.AsyncTimeout, token);
                    return true;
                }
            }
            catch (FabricException fe)
            {
                string message = $"Unable to create repairtask:{Environment.NewLine}{fe}";
                FabricHealerManager.RepairLogger.LogWarning(message);
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
                                         string taskIdPrefix,
                                         CancellationToken cancellationToken)
        {
            var allRecentFHRepairTasksCompleted =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            RepairTaskEngine.FHTaskIdPrefix,
                            RepairTaskStateFilter.Completed,
                            taskIdPrefix,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken);

            if (allRecentFHRepairTasksCompleted == null || allRecentFHRepairTasksCompleted.Count == 0)
            {
                return false;
            }

            var orderedRepairList = allRecentFHRepairTasksCompleted.OrderByDescending(o => o.CompletedTimestamp).ToList();

            if (taskIdPrefix == RepairTaskEngine.FHTaskIdPrefix)
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
            else if (taskIdPrefix == RepairTaskEngine.InfraTaskIdPrefix)
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
                                         string TaskIdPrefix,
                                         CancellationToken cancellationToken)
        {
            var allCurrentFHRepairTasks =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            TaskIdPrefix,
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

        public static async Task<int> GetCompletedRepairCountWithinTimeRangeAsync(
                                         TimeSpan timeWindow,
                                         TelemetryData repairData,
                                         CancellationToken cancellationToken,
                                         string repairAction = null)
        {
            var allRecentFHRepairTasksCompleted =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            RepairTaskEngine.FHTaskIdPrefix,
                            RepairTaskStateFilter.Completed,
                            null,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken);

            if (allRecentFHRepairTasksCompleted?.Count == 0)
            {
                return 0;
            }

            int count = 0;

            foreach (var repair in allRecentFHRepairTasksCompleted.Where(r => r.ResultStatus == RepairTaskResult.Succeeded))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (repair.CompletedTimestamp == null || !repair.CompletedTimestamp.HasValue)
                {
                    continue;
                }

                // Non-Machine repairs (FH is executor, custom repair ExecutorData supplied by FH.)
                if (repair.Executor == RepairConstants.FabricHealer)
                {
                    var fhExecutorData = JsonSerializationUtility.TryDeserializeObject(repair.ExecutorData, out RepairExecutorData exData) ? exData : null;

                    if (fhExecutorData == null || fhExecutorData.RepairData?.RepairPolicy == null)
                    {
                        continue;
                    }

                    if (repairData.RepairPolicy.RepairId != fhExecutorData.RepairData.RepairPolicy.RepairId)
                    {
                        continue;
                    }

                    // Note: Completed aborted/cancelled repair tasks should not block repairs if they are inside run interval.
                    if (DateTime.UtcNow.Subtract(repair.CompletedTimestamp.Value) <= timeWindow
                        && repair.Flags != RepairTaskFlags.CancelRequested && repair.Flags != RepairTaskFlags.AbortRequested)
                    {
                        count++;
                    }
                }
                // Machine repairs (IS is executor, ExecutorData supplied by IS. Custom FH repair id supplied as repair Description.)
                else if (repairData.RepairPolicy.RepairId != repair.Description)
                {
                    // Repair action string supplied.
                    if (!string.IsNullOrWhiteSpace(repairAction) && repairData.RepairPolicy.InfrastructureRepairName == repairAction)
                    {
                        if (DateTime.UtcNow.Subtract(repair.CompletedTimestamp.Value) <= timeWindow
                            && repair.Flags != RepairTaskFlags.CancelRequested && repair.Flags != RepairTaskFlags.AbortRequested)
                        {
                            count++;
                        }
                    }
                }
            }

            return count;
        }

        internal static async Task<TimeSpan> GetEntityCurrentHealthStateDurationAsync(EntityType entityType, string entityFilter, HealthState state, CancellationToken token)
        {
            HealthEventsFilter healthEventsFilter = new HealthEventsFilter();

            if (state == HealthState.Warning)
            {
                healthEventsFilter.HealthStateFilterValue = HealthStateFilter.Warning;
            }
            else if (state == HealthState.Error)
            {
                healthEventsFilter.HealthStateFilterValue = HealthStateFilter.Error;
            }
            else if (state == HealthState.Ok)
            {
                healthEventsFilter.HealthStateFilterValue = HealthStateFilter.Ok;
            }
            else
            {
                healthEventsFilter.HealthStateFilterValue = HealthStateFilter.None;
            }

            switch (entityType)
            {
                case EntityType.Application:
                    break;

                case EntityType.Service:
                    break;

                case EntityType.Machine:
                case EntityType.Node:

                    var queryDesc = new NodeHealthQueryDescription(entityFilter)
                    {
                        EventsFilter = healthEventsFilter
                    };
                    var nodeHealthList =
                        await FabricHealerManager.FabricClientSingleton.HealthManager.GetNodeHealthAsync(
                                    queryDesc, FabricHealerManager.ConfigSettings.AsyncTimeout, token);
                    
                    if (nodeHealthList == null || nodeHealthList.HealthEvents.Count == 0)
                    {
                        return TimeSpan.MinValue;
                    }

                    foreach (var nodeHealthEvent in nodeHealthList.HealthEvents)
                    {
                        if (nodeHealthEvent.IsExpired)
                        {
                            continue;
                        }

                        return DateTime.UtcNow.Subtract(nodeHealthEvent.SourceUtcTimestamp);
                    }

                    break;

                default:
                    return TimeSpan.MinValue;
            }
            
            return TimeSpan.MinValue;
        }

        internal static async Task<bool> IsRepairInPostProbationAsync(
                                            TimeSpan probationPeriod,
                                            string taskPrefixId,
                                            TelemetryData repairData,
                                            CancellationToken cancellationToken)
        {
            var allCurrentFHRepairTasks =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            taskPrefixId,
                            RepairTaskStateFilter.Completed,
                            null,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken);

            if (allCurrentFHRepairTasks == null || allCurrentFHRepairTasks.Count == 0)
            {
                return false;
            }

            var orderedRepairList = allCurrentFHRepairTasks.OrderByDescending(o => o.CompletedTimestamp).ToList();

            foreach (var repair in orderedRepairList)
            {
                if (repair.Description != repairData.RepairPolicy.RepairId)
                {
                    continue;
                }

                if (repair.CompletedTimestamp == null)
                {
                    continue;
                }

                if (repair.CompletedTimestamp.HasValue &&
                    DateTime.UtcNow.Subtract(repair.CompletedTimestamp.Value) <= probationPeriod)
                {
                    return true;
                }
            }

            return false;
        }
    }
}