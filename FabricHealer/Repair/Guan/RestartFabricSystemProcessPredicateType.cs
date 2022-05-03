// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using Guan.Logic;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using System.Threading.Tasks;

namespace FabricHealer.Repair.Guan
{
    public class RestartFabricSystemProcessPredicateType : PredicateType
    {
        private static RepairTaskManager RepairTaskManager;
        private static TelemetryData RepairData;
        private static RestartFabricSystemProcessPredicateType Instance;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

            }

            protected override async Task<bool> CheckAsync()
            {
                // Can only kill processes on the same node where the FH instance that took the job is running.
                if (RepairData.NodeName != RepairTaskManager.Context.NodeContext.NodeName)
                {
                    return false;
                }

                RepairData.RepairPolicy.RepairAction = RepairActionType.RestartProcess;
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
                            throw new GuanException($"Unsupported input: {Input.Arguments[i].Value.GetObjectValue().GetType()}");
                    }
                }

                // Try to schedule repair with RM.
                var repairTask = FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                    () => RepairTaskManager.ScheduleFabricHealerRepairTaskAsync(
                                            RepairData,
                                            RepairTaskManager.Token),
                                    RepairTaskManager.Token).GetAwaiter().GetResult();

                if (repairTask == null)
                {
                    return false;
                }

                // Try to execute repair (FH executor does this work and manages repair state).
                bool success = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () => RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync(
                                                repairTask,
                                                RepairData,
                                                RepairTaskManager.Token),
                                            RepairTaskManager.Token);
                return success;
            }
        }

        public static RestartFabricSystemProcessPredicateType Singleton(string name, RepairTaskManager repairTaskManager, TelemetryData repairData)
        {
            RepairTaskManager = repairTaskManager;
            RepairData = repairData;

            return Instance ??= new RestartFabricSystemProcessPredicateType(name);
        }

        private RestartFabricSystemProcessPredicateType(string name)
                 : base(name, true, 0)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}

