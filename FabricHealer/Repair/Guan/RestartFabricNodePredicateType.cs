// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Fabric.Repair;
using Guan.Logic;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using System.Threading.Tasks;
using System;
using System.Threading;

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
                    // Don't restart yourself.
                    if (RepairData.NodeName == FabricHealerManager.ServiceContext.NodeContext.NodeName)
                    {
                        return false;
                    }

                    await FabricHealerManager.RandomWaitAsync(FabricHealerManager.Token);
                }

                RepairData.RepairPolicy.RepairAction = RepairActionType.RestartFabricNode;
                RepairData.EntityType = EntityType.Node;
                RepairData.RepairPolicy.RepairIdPrefix = RepairConstants.FHTaskIdPrefix;
                RepairData.RepairPolicy.NodeImpactLevel = NodeImpactLevel.Restart;
                RepairData.RepairPolicy.MaxExecutionTime = TimeSpan.FromHours(4);
                
                // Set repair ownership to this instance of FH.
                RepairData.RepairPolicy.FHRepairExecutorNodeName = FabricHealerManager.ServiceContext.NodeContext.NodeName;

                if (Input.Arguments.Count == 1 && Input.Arguments[0].Value.GetEffectiveTerm().GetObjectValue().GetType() == typeof(TimeSpan))
                {
                    RepairData.RepairPolicy.MaxExecutionTime = (TimeSpan)Input.Arguments[0].Value.GetEffectiveTerm().GetObjectValue();
                }

                if (FabricHealerManager.ConfigSettings.EnableLogicRuleTracing)
                {
                    _ = await RepairTaskEngine.TryTraceCurrentlyExecutingRuleAsync(Input.ToString(), RepairData, FabricHealerManager.Token);
                }

                bool success = false;

                try
                {
                    // Block attempts to schedule Fabric node repair if one is already executing in the cluster.
                    if (await RepairTaskEngine.IsNodeRepairCurrentlyInFlightAsync(null, CancellationToken.None))
                    {
                        string message = $"A node repair is already in flight in the cluster. Will not schedule another node repair at this time.";

                        await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                $"NodeRepairAlreadyInProgress",
                                message,
                                CancellationToken.None,
                                null);

                        return false;
                    }

                    // Schedule repair task Fabric Node Restart. FH will also be the executor of the repair.
                    RepairTask repairTask = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                    () => RepairTaskManager.ScheduleFabricHealerRepairTaskAsync(
                                                            RepairData,
                                                            FabricHealerManager.Token),
                                                    FabricHealerManager.Token);

                    if (repairTask == null)
                    {
                        return false;
                    }

                    // Now execute the repair.
                    success = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () => RepairTaskManager.ExecuteFabricHealerRepairTaskAsync(
                                                repairTask,
                                                RepairData,
                                                FabricHealerManager.Token),
                                        FabricHealerManager.Token);
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
                                CancellationToken.None);
                    }

                    success = false;
                }

                return success;
            }
        }

        public static RestartFabricNodePredicateType Singleton(string name, TelemetryData repairData)
        {
            RepairData = repairData;
            return Instance ??= new RestartFabricNodePredicateType(name);
        }

        private RestartFabricNodePredicateType(string name)
                 : base(name, true, 0, 1)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}