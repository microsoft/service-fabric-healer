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

            if (repairData.RepairPolicy.RepairIdPrefix == RepairTaskEngine.FHTaskIdPrefix)
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
            else if (repairData.RepairPolicy.RepairIdPrefix == RepairTaskEngine.InfraTaskIdPrefix)
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

        public static async Task<int> GetCompletedRepairCountWithinTimeRangeAsync(
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

            int count = 0;

            foreach (var repair in allRecentFHRepairTasksCompleted.Where(r => r.ResultStatus == RepairTaskResult.Succeeded))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (repair.CompletedTimestamp == null || !repair.CompletedTimestamp.HasValue)
                {
                    continue;
                }

                // Non-Machine repairs scheduled and executed by FH.
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

                    if (DateTime.UtcNow.Subtract(repair.CompletedTimestamp.Value) <= timeWindow)
                    {
                        count++;
                    }
                }
                // Machine repairs scheduled by FH.
                else if (repairData.RepairPolicy.InfrastructureRepairName == repair.Action)
                {
                    if (DateTime.UtcNow.Subtract(repair.CompletedTimestamp.Value) <= timeWindow)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        public static async Task<int> GetCreatedRepairCountWithinTimeRangeAsync(
                                         TimeSpan timeWindow,
                                         TelemetryData repairData,
                                         CancellationToken cancellationToken)
        {
            var allRecentFHRepairTasksCreated =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            null,
                            RepairTaskStateFilter.Created,
                            null,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken);

            if (allRecentFHRepairTasksCreated?.Count == 0)
            {
                return 0;
            }

            int count = 0;

            foreach (RepairTask repair in allRecentFHRepairTasksCreated)
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

                    if (fhExecutorData == null || fhExecutorData.RepairData?.RepairPolicy == null)
                    {
                        continue;
                    }

                    if (repairData.RepairPolicy.RepairId != fhExecutorData.RepairData.RepairPolicy.RepairId)
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
        /// Returns the anount of time the target entity (application, node, etc) has been in the specified health state.
        /// </summary>
        /// <param name="entityType">EntityType</param>
        /// <param name="nameOrIdFilter">String representation of the target entity's name or ID (e.g., application name or node name or partition id)</param>
        /// <param name="healthState">Target HealthState to match.</param>
        /// <param name="token">CancellationToken</param>
        /// <returns></returns>
        internal static async Task<TimeSpan> GetEntityCurrentHealthStateDurationAsync(
                                                EntityType entityType, 
                                                string nameOrIdFilter,
                                                HealthState healthState,
                                                CancellationToken token)
        {
            HealthEventsFilter healthEventsFilter = new HealthEventsFilter();

            if (healthState == HealthState.Warning)
            {
                healthEventsFilter.HealthStateFilterValue = HealthStateFilter.Warning;
            }
            else if (healthState == HealthState.Error)
            {
                healthEventsFilter.HealthStateFilterValue = HealthStateFilter.Error;
            }
            else if (healthState == HealthState.Ok)
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
                    
                    var appqueryDesc = new ApplicationHealthQueryDescription(new Uri(nameOrIdFilter))
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

                        // How many times has the entity transitioned to Error health state in the last hour?
                        // This is not going to work if the same event is created in a cycle. TODO: figure out how to do this correctly.
                        if (healthEventsFilter.HealthStateFilterValue == HealthStateFilter.Error)
                        {
                            var appHealthErrorEvents =
                                 appHealth.HealthEvents.Where(
                                     evt => DateTime.UtcNow.Subtract(
                                         evt.LastErrorTransitionAt) <= TimeSpan.FromHours(1)).OrderByDescending(
                                             o => o.LastErrorTransitionAt);

                            int errorCount = appHealthErrorEvents.Count();

                            if (errorCount > 1)
                            {
                                return DateTime.UtcNow.Subtract(appHealthErrorEvents.First().LastErrorTransitionAt);
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

                case EntityType.Partition:

                    var partitionqueryDesc = new PartitionHealthQueryDescription(Guid.Parse(nameOrIdFilter))
                    {
                        EventsFilter = healthEventsFilter
                    };

                    try
                    {
                        var partitionHealth =
                            await FabricHealerManager.FabricClientSingleton.HealthManager.GetPartitionHealthAsync(
                                        partitionqueryDesc, FabricHealerManager.ConfigSettings.AsyncTimeout, token);

                        if (partitionHealth == null || partitionHealth.HealthEvents.Count == 0)
                        {
                            return TimeSpan.MinValue;
                        }

                        // How many times has the entity transitioned to Error health state in the last hour?
                        // This is not going to work if the same event is created in a cycle. TODO: figure out how to do this correctly.
                        if (healthEventsFilter.HealthStateFilterValue == HealthStateFilter.Error)
                        {
                            var partitionHealthErrorEvents =
                                 partitionHealth.HealthEvents.Where(
                                     evt => DateTime.UtcNow.Subtract(
                                         evt.LastErrorTransitionAt) <= TimeSpan.FromHours(1)).OrderByDescending(
                                             o => o.LastErrorTransitionAt);

                            int errorCount = partitionHealthErrorEvents.Count();

                            if (errorCount > 1)
                            {
                                return DateTime.UtcNow.Subtract(partitionHealthErrorEvents.First().LastErrorTransitionAt);
                            }
                        }

                        var partitionHealthEvents = partitionHealth.HealthEvents.OrderByDescending(o => o.SourceUtcTimestamp);
                        return DateTime.UtcNow.Subtract(partitionHealthEvents.First().SourceUtcTimestamp);
                    }
                    catch (FabricException)
                    {
                        return TimeSpan.MinValue;
                    }

                case EntityType.Service:

                    var servicequeryDesc = new ServiceHealthQueryDescription(new Uri(nameOrIdFilter))
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

                        // How many times has the entity transitioned to Error health state in the last hour?
                        // This is not going to work if the same event is created in a cycle. TODO: figure out how to do this correctly.
                        if (healthEventsFilter.HealthStateFilterValue == HealthStateFilter.Error)
                        {
                            var serviceHealthErrorEvents =
                                 serviceHealth.HealthEvents.Where(
                                     evt => DateTime.UtcNow.Subtract(
                                         evt.LastErrorTransitionAt) <= TimeSpan.FromHours(1)).OrderByDescending(
                                             o => o.LastErrorTransitionAt);

                            int errorCount = serviceHealthErrorEvents.Count();

                            if (errorCount > 1)
                            {
                                return DateTime.UtcNow.Subtract(serviceHealthErrorEvents.First().LastErrorTransitionAt);
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

                    var nodequeryDesc = new NodeHealthQueryDescription(nameOrIdFilter)
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

                        // How many times has the entity transitioned to Error health state in the last hour?
                        // This is not going to work if the same event is created in a cycle. TODO: figure out how to do this correctly.
                        if (healthEventsFilter.HealthStateFilterValue == HealthStateFilter.Error)
                        {
                            var nodeHealthErrorEvents =
                                 nodeHealth.HealthEvents.Where(
                                     evt => DateTime.UtcNow.Subtract(
                                         evt.LastErrorTransitionAt) <= TimeSpan.FromHours(2)).OrderByDescending(
                                             o => o.LastErrorTransitionAt);

                            int errorCount = nodeHealthErrorEvents.Count();

                            if (errorCount > 1)
                            {
                                return DateTime.UtcNow.Subtract(nodeHealthErrorEvents.First().LastErrorTransitionAt);
                            }
                        }

                        var nodeHealthEvents = nodeHealth.HealthEvents.OrderByDescending(o => o.SourceUtcTimestamp);
                        return DateTime.UtcNow.Subtract(nodeHealthEvents.First().SourceUtcTimestamp);
                    }
                    catch (Exception e) when (e is ArgumentException || e is FabricException || e is InvalidOperationException || e is TaskCanceledException || e is TimeoutException)
                    {
                        string message = $"Unable to get {healthState} health state duration for {entityType}: {e.Message}";
                        FabricHealerManager.RepairLogger.LogWarning(message);
                        return TimeSpan.MinValue;
                    }

                default:
                    return TimeSpan.MinValue;
            }
        }

        // TOTHINK: Should this look at any repair and apply a probation to it (so, not just FH-scheduled/executed repairs).
        // This mainly makes sense for node-level repairs (machine).
        internal static async Task<bool> IsRepairInPostProbationAsync(TimeSpan probationPeriod, TelemetryData repairData, CancellationToken cancellationToken)
        {
            var allCompletedFHRepairTasks =
                    await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                            null, // no prefix filter..
                            RepairTaskStateFilter.Completed,
                            null, // no executor name filter..
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken);

            if (allCompletedFHRepairTasks == null || allCompletedFHRepairTasks.Count == 0)
            {
                return false;
            }

            var orderedRepairList = allCompletedFHRepairTasks.OrderByDescending(o => o.CompletedTimestamp).ToList();

            foreach (var repair in orderedRepairList)
            {
                if (repair.CompletedTimestamp == null)
                {
                    continue;
                }

                if (repair.CompletedTimestamp.HasValue &&
                    DateTime.UtcNow.Subtract(repair.CompletedTimestamp.Value) < probationPeriod)
                {
                    return true;
                }
            }

            return false;
        }
    }
}