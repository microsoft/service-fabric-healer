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
    /// impl for Guan ScheduleMachineRepair predicate. Schedules repairs for machines.
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

                // Repair action name is required.
                string repairAction = (string)Input.Arguments[0].Value.GetObjectValue();

                /*
                    public const string SystemReboot = "System.Reboot";
                    public const string SystemReimageOS = "System.ReimageOS";
                    public const string SystemFullReimage = "System.FullReimage";
                    public const string SystemHostReboot = "System.Azure.HostReboot";
                    public const string SystemHostRepaveData = "System.Azure.HostRepaveData";
                */

                switch (repairAction)
                {
                    case RepairConstants.SystemReboot:
                    case RepairConstants.SystemHostReboot:
                        RepairData.RepairPolicy.RepairAction = RepairActionType.RebootMachine;
                        break;

                    case RepairConstants.SystemReimageOS:
                    case RepairConstants.SystemFullReimage:
                    case RepairConstants.SystemHostRepaveData:
                        RepairData.RepairPolicy.RepairAction = RepairActionType.ReimageOS;
                        break;

                    default:
                        throw new GuanException($"Unrecognized repair action name: {repairAction}. Repair actions are case sensitive.");
                }

                // FH does not execute repairs for VM level mitigation. InfrastructureService (IS) does,
                // so, FH schedules VM repairs via RM and the execution is taken care of by IS (the executor).
                // Block attempts to create duplicate repair tasks or more than specified concurrent machine-level repairs.
                var repairTaskEngine = new RepairTaskEngine();
                int count = Input.Arguments.Count;
                long maxConcurrentRepairs = 0;

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

                        case "Int64":
                            maxConcurrentRepairs = (long)Input.Arguments[i].Value.GetObjectValue();
                            break;

                        default:
                            throw new GuanException($"Unsupported input: {Input.Arguments[i].Value.GetObjectValue().GetType()}");
                    }
                }

                var isRepairAlreadyInProgress =
                        await repairTaskEngine.IsRepairInProgressAsync(
                                $"{RepairTaskEngine.InfrastructureServiceName}/{RepairData.NodeType}",
                                RepairData,
                                RepairTaskManager.Token,
                                maxConcurrentRepairs);

                if (isRepairAlreadyInProgress)
                {
                    string message = $"VM Repair {RepairData.RepairPolicy.RepairId} is already in progress" +
                                     $"{(maxConcurrentRepairs > 0 ? $" or max number of concurrent machine repairs ({maxConcurrentRepairs}) has been reached" : "")}. " +
                                     $"Will not attempt repair at this time.";

                    await RepairTaskManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"ScheduleMachineRepairPredicateType::{RepairData.RepairPolicy.RepairId}",
                            message,
                            RepairTaskManager.Token);

                    return false;
                }

                bool success = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () => RepairTaskManager.ScheduleInfrastructureRepairTask(
                                                RepairData,
                                                RepairTaskManager.Token),
                                        RepairTaskManager.Token);
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
