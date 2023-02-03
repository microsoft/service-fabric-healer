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
        private static RepairExecutorData RepairExecutorData;
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
                int count = Input.Arguments.Count;

                for (int i = 0; i < count; i++)
                {
                    var typeString = Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue().GetType().Name;
                    switch (typeString)
                    {
                        case "TimeSpan":
                            RepairData.RepairPolicy.MaxTimePostRepairHealthCheck = (TimeSpan)Input.Arguments[i].Value.GetObjectValue();
                            break;

                        case "Boolean":
                            RepairData.RepairPolicy.DoHealthChecks = (bool)Input.Arguments[i].Value.GetObjectValue();
                            break;

                        default:
                            throw new GuanException(
                                $"RestartFabricNodePredicateType failure. Unsupported argument type: {Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue().GetType().Name}");
                    }
                }

                RepairTask repairTask;
                bool success;

                // This means it's a resumed repair.
                if (RepairExecutorData != null)
                {
                    // Historical info, like what step the healer was in when the node went down, is contained in the
                    // executordata instance.
                    repairTask = await RepairTaskEngine.CreateFabricHealerRepairTask(RepairExecutorData, FabricHealerManager.Token);
                    success = await RepairTaskManager.ExecuteFabricHealerRepairTaskAsync(
                                        repairTask,
                                        RepairData,
                                        FabricHealerManager.Token);
                    return success;
                }

                // Block attempts to create node-level repair tasks if one is already running in the cluster.
                var isNodeRepairAlreadyInProgress =
                    await RepairTaskEngine.IsRepairInProgressAsync(
                            RepairConstants.FHTaskIdPrefix,
                            RepairData,
                            FabricHealerManager.Token);

                if (isNodeRepairAlreadyInProgress)
                {
                    string message =
                    $"A repair for node {RepairData.NodeName} is already in progress in the cluster. Will not node attempt repair at this time.";

                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"RestartFabricNodePredicateType::{RepairData.RepairPolicy.RepairId}",
                            message,
                            FabricHealerManager.Token);

                    return false;
                }

                // Try to schedule repair with RM.
                repairTask = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                    () => RepairTaskManager.ScheduleFabricHealerRepairTaskAsync(
                                            RepairData,
                                            FabricHealerManager.Token),
                                    FabricHealerManager.Token);

                if (repairTask == null)
                {
                    return false;
                }

                // Try to execute custom repair (FH executor).
                success = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                    () => RepairTaskManager.ExecuteFabricHealerRepairTaskAsync(
                                            repairTask,
                                            RepairData,
                                            FabricHealerManager.Token),
                                    FabricHealerManager.Token);
                return success;
            }
        }

        public static RestartFabricNodePredicateType Singleton(
                        string name,
                        RepairExecutorData repairExecutorData,
                        TelemetryData repairData)
        {
            RepairExecutorData = repairExecutorData;
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