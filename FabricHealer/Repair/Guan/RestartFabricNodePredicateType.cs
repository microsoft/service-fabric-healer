// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric.Repair;
using Guan.Logic;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using System.Threading.Tasks;

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
                RepairData.RepairPolicy.RepairAction = RepairActionType.RestartFabricNode;
                RepairData.EntityType = EntityType.Node;
                RepairData.RepairPolicy.RepairIdPrefix = RepairConstants.FHTaskIdPrefix;
                RepairData.RepairPolicy.NodeImpactLevel = NodeImpactLevel.Restart;

                if (FabricHealerManager.ConfigSettings.EnableLogicRuleTracing)
                {
                    _ = await RepairTaskEngine.TryTraceCurrentlyExecutingRuleAsync(Input.ToString(), RepairData, FabricHealerManager.Token);
                }

                // Block attempts to create node-level repair tasks if one is already running in the cluster.
                var isNodeRepairAlreadyInProgress =
                    await RepairTaskEngine.IsRepairInProgressAsync(RepairData, FabricHealerManager.Token);

                if (isNodeRepairAlreadyInProgress)
                {
                    string message =
                    $"A repair for Fabric node {RepairData.NodeName} is already in progress in the cluster. Will not node attempt repair at this time.";

                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"RestartFabricNode::{RepairData.RepairPolicy.RepairId}",
                            message,
                            FabricHealerManager.Token,
                            RepairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return false;
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

                // Now execute the repair.
                bool success = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () => RepairTaskManager.ExecuteFabricHealerRepairTaskAsync(
                                                repairTask,
                                                RepairData,
                                                FabricHealerManager.Token),
                                        FabricHealerManager.Token);

                return success;
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