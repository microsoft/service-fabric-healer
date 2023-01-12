﻿// ------------------------------------------------------------
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
    /// <summary>
    /// Backing impl for ScheduleMachineRepair Guan predicate used in logic rules. This predicate schedules SF InfrastructureService-executed repairs for host machines.
    /// </summary>
    public class ScheduleMachineRepairPredicateType : PredicateType
    {
        private static RepairTaskManager RepairTaskManager;
        private static TelemetryData RepairData;
        private static ScheduleMachineRepairPredicateType Instance;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

            }

            protected override async Task<bool> CheckAsync()
            {
                if (Input.Arguments.Count == 0)
                {
                    throw new GuanException("You must provide a repair action name for Infrastructure-level repairs as first argument.");
                }

                // FH does not execute repairs for VM level mitigation. InfrastructureService (IS) does,
                // so, FH schedules VM repairs via RM and the execution is taken care of by IS (the executor).
                // Block attempts to create duplicate repair tasks or more than specified concurrent machine-level repairs.
                var repairTaskEngine = new RepairTaskEngine();
                int count = Input.Arguments.Count;
                string repairAction = null;

                for (int i = 0; i < count; i++)
                {
                    var typeString = Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue().GetType().Name;

                    switch (typeString)
                    {
                        case "String":
                            repairAction = (string)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            RepairData.RepairPolicy.InfrastructureRepairName = repairAction; 
                            break;

                        case "Boolean":
                            RepairData.RepairPolicy.DoHealthChecks = (bool)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        default:
                            throw new GuanException(
                                "Failure in ScheduleMachineRepair. Unsupported argument type specified: " +
                                $"{Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue().GetType().Name}{Environment.NewLine}" +
                                $"Only String and Boolean argument types are supported by this predicate.");
                    }
                }

                bool isRepairAlreadyInProgress =
                    await repairTaskEngine.IsNodeLevelRepairCurrentlyInFlightAsync(RepairData, FabricHealerManager.Token);

                if (isRepairAlreadyInProgress)
                {
                    string message = 
                        $"Machine Repair is already in progress for node {RepairData.NodeName}. Will not schedule machine repair at this time.";

                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"ScheduleMachineRepair::{RepairData.RepairPolicy.InfrastructureRepairName}",
                            message,
                            FabricHealerManager.Token);

                    return false;
                }

                // Attempt to schedule an Infrastructure Repair Job (where IS is the executor).
               bool success = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () => RepairTaskManager.ScheduleInfrastructureRepairTask(
                                                RepairData,
                                                FabricHealerManager.Token),
                                        FabricHealerManager.Token);
                return success;
            }
        }

        public static ScheduleMachineRepairPredicateType Singleton(string name, RepairTaskManager repairTaskManager, TelemetryData repairData)
        {
            RepairTaskManager = repairTaskManager;
            RepairData = repairData;
            return Instance ??= new ScheduleMachineRepairPredicateType(name);
        }

        private ScheduleMachineRepairPredicateType(string name)
                 : base(name, true, 1, 2)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}