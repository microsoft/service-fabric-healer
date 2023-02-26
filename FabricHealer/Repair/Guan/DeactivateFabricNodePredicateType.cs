// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Utilities.Telemetry;
using FabricHealer.Utilities;
using Guan.Logic;
using System;
using System.Fabric.Repair;
using System.Threading.Tasks;

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
                RepairData.RepairPolicy.RepairIdPrefix = RepairConstants.FHTaskIdPrefix;
                RepairData.RepairPolicy.RepairId = $"DeactivateNode::{RepairData.NodeName}";

                if (FabricHealerManager.ConfigSettings.EnableLogicRuleTracing)
                {
                    _ = await RepairTaskEngine.TryTraceCurrentlyExecutingRule(Input.ToString(), RepairData);
                }

                int count = Input.Arguments.Count;

                for (int i = 0; i < count; i++)
                {
                    var typeString = Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue().GetType().Name;

                    switch (typeString)
                    {
                        case "Boolean" when i == 0 && count == 4 || Input.Arguments[i].Name.ToLower() == "dohealthchecks":
                            RepairData.RepairPolicy.DoHealthChecks = (bool)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        case "TimeSpan" when i == 1 && count == 4 || Input.Arguments[i].Name.ToLower() == "maxwaittimeforhealthstateok":
                            RepairData.RepairPolicy.MaxTimePostRepairHealthCheck = (TimeSpan)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        case "TimeSpan" when i == 2 && count == 4 || Input.Arguments[i].Name.ToLower() == "maxexecutiontime":
                            RepairData.RepairPolicy.MaxExecutionTime = (TimeSpan)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        case "String" when i == 3 && count == 4 || Input.Arguments[i].Name.ToLower() == "impactlevel":
                            
                            string value = Input.Arguments[i].Value.GetEffectiveTerm().GetStringValue().ToLower();
                            
                            if (value == "removedata")
                            {
                                RepairData.RepairPolicy.NodeImpactLevel = NodeImpactLevel.RemoveData;
                            }
                            else if (value == "removenode")
                            {
                                RepairData.RepairPolicy.NodeImpactLevel = NodeImpactLevel.RemoveNode;
                            }
                            else if (value == "restart")
                            {
                                RepairData.RepairPolicy.NodeImpactLevel = NodeImpactLevel.Restart;
                            }
                            else
                            {
                                RepairData.RepairPolicy.NodeImpactLevel = NodeImpactLevel.None;
                            }
                            
                            break;

                        default:
                            throw new GuanException($"Unsupported argument type for RestartFabricNode: {typeString}");
                    }
                }

                var isNodeRepairAlreadyInProgress =
                    await RepairTaskEngine.IsRepairInProgressAsync(RepairData, FabricHealerManager.Token);

                if (isNodeRepairAlreadyInProgress)
                {
                    string message =
                    $"A repair for Fabric node {RepairData.NodeName} is already in progress in the cluster.";

                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"DeactivateFabricNode::{RepairData.RepairPolicy.RepairId}",
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
                    : base(name, true, 0)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}

