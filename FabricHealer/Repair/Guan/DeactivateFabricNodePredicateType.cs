// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Utilities.Telemetry;
using FabricHealer.Utilities;
using Guan.Logic;
using System.Fabric.Repair;
using System.Threading.Tasks;
using System;
using System.Linq;

namespace FabricHealer.Repair.Guan
{
    internal class DeactivateFabricNodePredicateType : PredicateType
    {
        private static TelemetryData RepairData;
        private static DeactivateFabricNodePredicateType Instance;
        
        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
            : base(input, constraint, context)
            {

            }

            protected override async Task<bool> CheckAsync()
            {
                RepairData.RepairPolicy.RepairAction = RepairActionType.DeactivateNode;
                RepairData.RepairPolicy.RepairIdPrefix = RepairConstants.InfraTaskIdPrefix;
                RepairData.RepairPolicy.InfrastructureRepairName = RepairConstants.DeactivateFabricNode;
                RepairData.RepairPolicy.RepairId = $"{RepairConstants.DeactivateFabricNode}::{RepairData.NodeName}";
                RepairData.RepairPolicy.NodeImpactLevel = NodeImpactLevel.Restart;
                
                // Unlimited.
                RepairData.RepairPolicy.MaxExecutionTime = TimeSpan.Zero;

                if (FabricHealerManager.ConfigSettings.EnableLogicRuleTracing)
                {
                    _ = await RepairTaskEngine.TryTraceCurrentlyExecutingRuleAsync(Input.ToString(), RepairData, FabricHealerManager.Token);
                }

                long args = Input.Arguments.Count;

                for (int i = 0; i < args; i++)
                {
                    var typeString = Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue().GetType().Name;

                    switch (typeString)
                    {
                        case "TimeSpan":

                            RepairData.RepairPolicy.MaxExecutionTime = (TimeSpan)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        // This only makes sense for Machine-level repair rules, where you can specify any string for machine repair action that is supported in your SF configuration.
                        // Otherwise, FH already knows what you mean with GetRepairHistory([TimeSpan value]), given the repair context (which FH creates).
                        case "String":

                            string value = Input.Arguments[i].Value.GetEffectiveTerm().GetStringValue().ToLower();

                            if (value == "removedata")
                            {
                                RepairData.RepairPolicy.NodeImpactLevel = NodeImpactLevel.RemoveData;
                            }
                            else if (value == "removenode")
                            {
                                RepairData.RepairPolicy.NodeImpactLevel = NodeImpactLevel.RemoveNode;
                            }
                            else
                            {
                                RepairData.RepairPolicy.NodeImpactLevel = NodeImpactLevel.Restart;
                            }
                            break;

                        default:

                            throw new GuanException(
                                "DeactivateFabricNodePredicateType failure. Unsupported argument type: " +
                                $"{Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue().GetType().Name}");
                    }
                }

                var isNodeRepairAlreadyInProgress =
                    await RepairTaskEngine.IsNodeLevelRepairCurrentlyInFlightAsync(RepairData, FabricHealerManager.Token);

                if (isNodeRepairAlreadyInProgress)
                {
                    string message =
                        $"A repair for Fabric node {RepairData.NodeName} is already in progress in the cluster.";

                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            RepairData.RepairPolicy.RepairId,
                            message,
                            FabricHealerManager.Token,
                            RepairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return false;
                }

                // Try to schedule the Deactivate repair with RM (RM will deactivate the node, not FH).
                RepairTask repairTask = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () => RepairTaskManager.ScheduleFabricHealerRepairTaskAsync(
                                                RepairData,
                                                FabricHealerManager.Token),
                                        FabricHealerManager.Token);
                if (repairTask == null)
                {
                    return false;
                }

                return true;
            }
        }

        public static DeactivateFabricNodePredicateType Singleton(string name, TelemetryData repairData)
        {
            RepairData = repairData;
            return Instance ??= new DeactivateFabricNodePredicateType(name);
        }

        private DeactivateFabricNodePredicateType(string name)
                    : base(name, true, 0, 2)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}

