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
            FabricClient fabricClient,
            string executorName,
            List<RepairTaskState> desiredStates)
        {
            IList<RepairTask> repairTaskList = await fabricClient.RepairManager.GetRepairTaskListAsync(
                taskId, 
                RepairTaskStateFilter.All,
                executorName).ConfigureAwait(true);

            return desiredStates.Any(desiredState => repairTaskList.Count(rt => rt.State == desiredState) > 0);
        }

        /// <summary>
        /// Cancels a repair task based on its current state
        /// </summary>
        /// <param name="repairTask"><see cref="RepairTask"/> to be cancelled</param>
        /// <returns></returns>
        public static async Task CancelRepairTaskAsync(RepairTask repairTask, FabricClient fabricClient)
        {
            switch (repairTask.State)
            {
                case RepairTaskState.Restoring:
                case RepairTaskState.Completed:

                    break;

                case RepairTaskState.Created:
                case RepairTaskState.Claimed:
                case RepairTaskState.Preparing:

                    _ = await fabricClient.RepairManager.CancelRepairTaskAsync(
                        repairTask.TaskId,
                        repairTask.Version,
                        true).ConfigureAwait(false);

                    break;

                case RepairTaskState.Approved:
                case RepairTaskState.Executing:

                    repairTask.State = RepairTaskState.Restoring;
                    repairTask.ResultStatus = RepairTaskResult.Cancelled;
                    _ = await fabricClient.RepairManager.UpdateRepairExecutionStateAsync(repairTask).ConfigureAwait(false);

                    break;

                case RepairTaskState.Invalid:

                    break;

                default:

                    throw new Exception($"Repair task {repairTask.TaskId} is in invalid state {repairTask.State}");
            }
        }

        public static async Task<bool> CompleteCustomActionRepairJobAsync(
            RepairTask repairTask,
            FabricClient fabricClient,
            StatelessServiceContext context,
            CancellationToken token)
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

                _ = await fabricClient.RepairManager.UpdateRepairExecutionStateAsync(
                        repairTask,
                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                        token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                var telemetryUtilities = new TelemetryUtilities(fabricClient, context);

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Error,
                    "FabricRepairTasks.CompleteCustomActionRepairJobAsync",
                     $"Failed to Complete Repair Task {repairTask.TaskId} with " +
                     $"Unhandled Exception:{Environment.NewLine}{e}",
                     token).ConfigureAwait(false);
                
                if (e is FabricException || e is TaskCanceledException || e is OperationCanceledException)
                {
                    return false;
                }

                throw;
            }

            return true;
        }

        public static async Task<RepairTask> ScheduleRepairTaskAsync(
            RepairConfiguration repairConfiguration,
            RepairExecutorData executorData,
            string executorName,
            FabricClient fabricClient,
            CancellationToken token)
        {
            var repairTaskEngine = new RepairTaskEngine(fabricClient);

            RepairTask repairTask;

            var repairAction = repairConfiguration.RepairPolicy.CurrentAction;

            switch (repairAction)
            {
                case RepairAction.RestartVM:
                    
                    repairTask = repairTaskEngine.CreateVmRebootTask(
                        repairConfiguration,
                        executorName);

                    break;

                case RepairAction.ReimageVM:
                    // This does not work for general use case. This has to be redesigned and reimplemented. -CT
                    /*repairTask = repairTaskEngine.CreateVmReImageTask(
                        repairConfiguration,
                        executorName);*/

                    break;

                case RepairAction.DeleteFiles:
                case RepairAction.RestartCodePackage:
                case RepairAction.RestartFabricNode:
                case RepairAction.RestartReplica:

                    repairTask = repairTaskEngine.CreateFabricHealerRmRepairTask(
                        repairConfiguration,
                        executorData);

                    break;

                default:

                    FabricHealerManager.RepairLogger.LogWarning("Unknown FabricRepairAction specified.");
                    return null;
            }

            bool success = await TryCreateRepairTaskAsync(
                                    fabricClient,
                                    repairTask,
                                    repairConfiguration,
                                    token).ConfigureAwait(false);

            if (success)
            {
                return repairTask;
            }

            return null;
        }

        private static async Task<bool> TryCreateRepairTaskAsync(
            FabricClient fabricClient, 
            RepairTask repairTask,
            RepairConfiguration repairConfiguration,
            CancellationToken token)
        {
            if (repairTask == null)
            {
                return false;
            }

            try
            {
                var repairTaskEngine = new RepairTaskEngine(fabricClient);
                var isRepairAlreadyInProgress =
                    await repairTaskEngine.IsFHRepairTaskRunningAsync(
                        repairTask.Executor,
                        repairConfiguration,
                        token).ConfigureAwait(false);

                if (!isRepairAlreadyInProgress)
                {
                    _ = await fabricClient.RepairManager.CreateRepairTaskAsync(
                            repairTask,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            token).ConfigureAwait(false);

                    return true;
                }
            }
            catch (FabricException fe)
            {
                string message =
                    $"Unable to create repairtask:{Environment.NewLine}{fe}";

                FabricHealerManager.RepairLogger.LogWarning(message);

                FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "FabricRepairTasks::TryCreateRepairTaskAsync",
                    message,
                    token).GetAwaiter().GetResult();
            }

            return false;
        }

        public static async Task<long> SetFabricRepairJobStateAsync(
            RepairTask repairTask, 
            RepairTaskState repairState,
            RepairTaskResult repairResult,
            FabricClient fabricClient,
            CancellationToken token)
        {
            repairTask.State = repairState;
            repairTask.ResultStatus = repairResult;

           return  
                await fabricClient.RepairManager.UpdateRepairExecutionStateAsync(
                            repairTask,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            token).ConfigureAwait(false);
        }

        public static async Task<IEnumerable<Service>> GetInfrastructureServiceInstancesAsync(
            FabricClient fabricClient,
            CancellationToken cancellationToken)
        {
            var allSystemServices =
                await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                () =>
                    fabricClient.QueryManager.GetServiceListAsync(
                        new Uri("fabric:/System"),
                        null,
                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                        cancellationToken),
                cancellationToken).ConfigureAwait(false);

            var infraInstances = allSystemServices.Where(
                i => i.ServiceTypeName.Equals(
                    RepairConstants.InfrastructureServiceType,
                    StringComparison.InvariantCultureIgnoreCase));

            return infraInstances;
        }

        public static async Task<bool> IsLastCompletedFHRepairTaskWithinTimeRange(
            TimeSpan interval, 
            FabricClient fabricClient,
            TelemetryData foHealthData,
            CancellationToken cancellationToken)
        {
            var allRecentFHRepairTasksCompleted =
                            await fabricClient.RepairManager.GetRepairTaskListAsync(
                                RepairTaskEngine.FHTaskIdPrefix,
                                RepairTaskStateFilter.Completed,
                                null,
                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                cancellationToken).ConfigureAwait(true);

            if (allRecentFHRepairTasksCompleted?.Count == 0)
            {
                return false;
            }

            foreach (var repair in allRecentFHRepairTasksCompleted.Where(r => r.ResultStatus == RepairTaskResult.Succeeded))
            {
                var fhExecutorData =
                    SerializationUtility.TryDeserialize(repair.ExecutorData, out RepairExecutorData exData) ? exData : null;

                // Non-VM repairs (FH is executor, custom repair ExecutorData supplied by FH.)
                if (fhExecutorData != null)
                {
                    if (foHealthData.RepairId != fhExecutorData.CustomIdentificationData)
                    {
                        continue;
                    }

                    if (repair.CompletedTimestamp == null || !repair.CompletedTimestamp.HasValue)
                    {
                        return false;
                    }

                    // Note: Completed aborted/cancelled repair tasks should not block repairs if they are inside run interval.
                    if (DateTime.UtcNow.Subtract(repair.CompletedTimestamp.Value) <= interval
                        && repair.Flags != RepairTaskFlags.CancelRequested && repair.Flags != RepairTaskFlags.AbortRequested)
                    {
                        return true;
                    }
                }
                // VM repairs (IS is executor, ExecutorData supplied by IS. Custom FH repair id supplied as repair Description.)
                else if (repair.Executor == $"fabric:/System/InfrastructureService/{foHealthData.NodeType}" && repair.Description == foHealthData.RepairId)
                {
                    if (repair.CompletedTimestamp == null || !repair.CompletedTimestamp.HasValue)
                    {
                        return false;
                    }

                    // Note: Completed aborted/cancelled repair tasks should not block repairs if they are inside run interval.
                    if (DateTime.UtcNow.Subtract(repair.CompletedTimestamp.Value) <= interval
                        && repair.Flags != RepairTaskFlags.CancelRequested && repair.Flags != RepairTaskFlags.AbortRequested)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}