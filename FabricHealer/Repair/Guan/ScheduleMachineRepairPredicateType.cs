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

                        case "TimeSpan":
                            RepairData.RepairPolicy.MaxTimePostRepairHealthCheck = (TimeSpan)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        case "Boolean":
                            RepairData.RepairPolicy.DoHealthChecks = (bool)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;
                        // Guan logic defaults to long for numeric types.
                        case "Int64":
                            RepairData.RepairPolicy.MaxConcurrentRepairs = (long)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        default:
                            throw new GuanException(
                                "Failure in ScheduleMachineRepairPredicateType. Unsupported argument type specified: " +
                                $"{Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue().GetType().Name}{Environment.NewLine}" +
                                $"Only String, TimeSpan, Boolean and Int32/64 argument types are supported by this predicate.");
                    }
                }

                bool isRepairAlreadyInProgress =
                        await repairTaskEngine.IsRepairInProgressAsync(
                                RepairData.EntityType == EntityType.Machine ? RepairTaskEngine.InfraTaskIdPrefix : RepairTaskEngine.FHTaskIdPrefix,
                                RepairData,
                                FabricHealerManager.Token);

                if (isRepairAlreadyInProgress)
                {
                    string message = 
                        $"Machine Repair is already in progress for node {RepairData.NodeName}. Will not schedule machine repair at this time.";

                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"ScheduleMachineRepairPredicateType::{RepairData.RepairPolicy.RepairId}",
                            message,
                            FabricHealerManager.Token);

                    return false;
                }

                int outstandingRepairCount = 
                    await repairTaskEngine.GetOutstandingRepairCount(taskIdPrefix: RepairTaskEngine.InfraTaskIdPrefix, FabricHealerManager.Token);

                if (RepairData.RepairPolicy.MaxConcurrentRepairs > 0 && outstandingRepairCount >= RepairData.RepairPolicy.MaxConcurrentRepairs)
                {
                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                           LogLevel.Info,
                           "ScheduleMachineRepairPredicateType::MaxOustandingRepairs",
                           $"The number of outstanding machine repairs is currently at the maximum specified threshold ({RepairData.RepairPolicy.MaxConcurrentRepairs}). " +
                           $"Will not schedule any other machine repairs at this time.",
                           FabricHealerManager.Token);

                    return false;
                }

                // TODO: Experiment with Guan context information for rules (what rule executed for repair).

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
                 : base(name, true, 0)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}
