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
    public class RestartVMPredicateType : PredicateType
    {
        private static RepairTaskManager RepairTaskManager;
        private static TelemetryData RepairData;
        private static RestartVMPredicateType Instance;

        private class Resolver : BooleanPredicateResolver
        {
            private readonly RepairConfiguration repairConfiguration;

            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {
                repairConfiguration = new RepairConfiguration
                {
                    AppName = null,
                    ErrorCode = RepairData.Code,
                    EntityType = RepairData.EntityType,
                    NodeName = RepairData.NodeName,
                    NodeType = RepairData.NodeType,
                    PartitionId = default,
                    ReplicaOrInstanceId = 0,
                    ServiceName = null,
                    MetricValue = RepairData.Value,
                    RepairPolicy = new RepairPolicy
                    {
                        RepairAction = RepairActionType.RestartVM,
                        RepairId = RepairData.RepairId,
                        TargetType = RepairTargetType.VirtualMachine
                    },
                    EventSourceId = RepairData.Source,
                    EventProperty = RepairData.Property
                };
            }

            protected override async Task<bool> CheckAsync()
            {
                int count = Input.Arguments.Count;

                for (int i = 0; i < count; i++)
                {
                    var typeString = Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue().GetType().Name;
                    switch (typeString)
                    {
                        case "TimeSpan":
                            repairConfiguration.RepairPolicy.MaxTimePostRepairHealthCheck = (TimeSpan)Input.Arguments[i].Value.GetObjectValue();
                            break;

                        case "Boolean":
                            repairConfiguration.RepairPolicy.DoHealthChecks = (bool)Input.Arguments[i].Value.GetObjectValue();
                            break;

                        default:
                            throw new GuanException($"Unsupported input: {Input.Arguments[i].Value.GetObjectValue().GetType()}");
                    }
                }

                // FH does not execute repairs for VM level mitigation. InfrastructureService (IS) does,
                // so, FH schedules VM repairs via RM and the execution is taken care of by IS (the executor).
                // Block attempts to create duplicate repair tasks.
                var repairTaskEngine = new RepairTaskEngine(RepairTaskManager.FabricClientInstance);
                var isRepairAlreadyInProgress =
                    await repairTaskEngine.IsFHRepairTaskRunningAsync(
                                            $"{RepairTaskEngine.InfrastructureServiceName}/{RepairData.NodeType}",
                                            repairConfiguration,
                                            RepairTaskManager.Token).ConfigureAwait(false);
                
                if (isRepairAlreadyInProgress)
                {
                    string message = $"VM Repair {RepairData.RepairId} is already in progress. Will not attempt repair at this time.";

                    await RepairTaskManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                            LogLevel.Info,
                                                            $"RestartVMPredicateType::{RepairData.RepairId}",
                                                            message,
                                                            RepairTaskManager.Token).ConfigureAwait(false);
                    return false;
                }

                bool success = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                            () => RepairTaskManager.ExecuteRMInfrastructureRepairTask(
                                                                                        repairConfiguration,
                                                                                        RepairTaskManager.Token),
                                                            RepairTaskManager.Token).ConfigureAwait(false);
                return success;
            }
        }

        public static RestartVMPredicateType Singleton(string name, RepairTaskManager repairTaskManager, TelemetryData repairData)
        {
            RepairTaskManager = repairTaskManager;
            RepairData = repairData;

            return Instance ??= new RestartVMPredicateType(name);
        }

        private RestartVMPredicateType(string name)
                 : base(name, true, 0)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}
