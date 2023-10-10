// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Fabric.Repair;
using Guan.Logic;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace FabricHealer.Repair.Guan
{
    public class RestartFabricNodePredicateType : PredicateType
    {
        private static TelemetryData RepairData;
        private static RestartFabricNodePredicateType Instance;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

            }

            protected override async Task<bool> CheckAsync()
            {
                if (FabricHealerManager.InstanceCount is (-1) or > 1)
                { 
                    await FabricHealerManager.RandomWaitAsync();
                }

                RepairData.RepairPolicy.RepairAction = RepairActionType.RestartFabricNode;
                RepairData.EntityType = EntityType.Node;
                RepairData.RepairPolicy.RepairIdPrefix = RepairConstants.FHTaskIdPrefix;
                RepairData.RepairPolicy.NodeImpactLevel = NodeImpactLevel.Restart;
                
                // TODO: Make this configurable?
                RepairData.RepairPolicy.MaxExecutionTime = TimeSpan.FromHours(2);

                if (FabricHealerManager.ConfigSettings.EnableLogicRuleTracing)
                {
                    _ = await RepairTaskEngine.TryTraceCurrentlyExecutingRuleAsync(Input.ToString(), RepairData, FabricHealerManager.Token);
                }

                // Try to schedule repair with RM for Fabric Node Restart (FH will also be the executor of the repair).
                RepairTask repairTask = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                () => RepairTaskManager.ScheduleFabricHealerRepairTaskAsync(
                                                        RepairData,
                                                        FabricHealerManager.Token),
                                                FabricHealerManager.Token);
                if (repairTask == null)
                {
                    return false;
                }

                // Block attempts to execute node-level repair tasks if one is already executing in the cluster.
                var nodeRepairsAlreadyExecuting =
                    await RepairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairConstants.FHTaskIdPrefix, FabricHealerManager.Token, null, RepairTaskStateFilter.Preparing | RepairTaskStateFilter.ReadyToExecute);

                if (nodeRepairsAlreadyExecuting != null && nodeRepairsAlreadyExecuting.Count > 0)
                {
                    string message =
                    $"A repair for Fabric node {RepairData.NodeName} is already executing in the cluster. Will not attempt another Fabric node repair at this time.";

                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"RestartFabricNode::{RepairData.RepairPolicy.RepairId}",
                            message,
                            FabricHealerManager.Token,
                            RepairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return false;
                }

                // MaxExecutionTime impl.
                using (CancellationTokenSource tokenSource = new())
                {
                    using (var linkedCTS = CancellationTokenSource.CreateLinkedTokenSource(tokenSource.Token, FabricHealerManager.Token))
                    {
                        tokenSource.CancelAfter(RepairData.RepairPolicy.MaxExecutionTime);
                        tokenSource.Token.Register(() =>
                        {
                            _ = FabricHealerManager.TryCleanUpOrphanedFabricHealerRepairJobsAsync();
                        });

                        bool success = false;

                        try
                        {
                            // Now execute the repair.
                            success = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                () => RepairTaskManager.ExecuteFabricHealerRepairTaskAsync(
                                                        repairTask,
                                                        RepairData,
                                                        linkedCTS.Token),
                                                linkedCTS.Token);
                            return success;
                        }
                        catch (Exception e) when (e is not OutOfMemoryException)
                        {
                            if (e is not TaskCanceledException and not OperationCanceledException)
                            {
                                string message = $"Failed to execute {RepairData.RepairPolicy.RepairAction} for repair {RepairData.RepairPolicy.RepairId}: {e.Message}";
#if DEBUG
                                message += $"{Environment.NewLine}{e}";
#endif
                                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "RestartFabricNodePredicateType::HandledException",
                                        message,
                                        FabricHealerManager.Token);
                            }

                            success = false;
                        }

                        // Best effort FH repair job cleanup retry.
                        if (!success && linkedCTS.IsCancellationRequested)
                        {
                            await FabricHealerManager.TryCleanUpOrphanedFabricHealerRepairJobsAsync();
                            return true;
                        }

                        return success;
                    }
                }
            }
        }

        public static RestartFabricNodePredicateType Singleton(string name, TelemetryData repairData)
        {
            RepairData = repairData;
            return Instance ??= new RestartFabricNodePredicateType(name);
        }

        private RestartFabricNodePredicateType(string name)
                 : base(name, true, 0)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}