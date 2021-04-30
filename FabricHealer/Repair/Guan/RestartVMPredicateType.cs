// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using Guan.Common;
using Guan.Logic;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;

namespace FabricHealer.Repair.Guan
{
    public class RestartVMPredicateType : PredicateType
    {
        private static RepairTaskManager RepairTaskManager;
        private static TelemetryData FOHealthData;
        private static RestartVMPredicateType Instance;

        private class Resolver : BooleanPredicateResolver
        {
            private readonly RepairConfiguration repairConfiguration;

            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {
                repairConfiguration = new RepairConfiguration
                {
                    AppName = !string.IsNullOrWhiteSpace(FOHealthData.ApplicationName) ? new Uri(FOHealthData.ApplicationName) : null,
                    FOErrorCode = FOHealthData.Code,
                    NodeName = FOHealthData.NodeName,
                    NodeType = FOHealthData.NodeType,
                    PartitionId = !string.IsNullOrWhiteSpace(FOHealthData.PartitionId) ? new Guid(FOHealthData.PartitionId) : default,
                    ReplicaOrInstanceId = !string.IsNullOrWhiteSpace(FOHealthData.ReplicaId) ? long.Parse(FOHealthData.ReplicaId) : default,
                    ServiceName = !string.IsNullOrWhiteSpace(FOHealthData.ServiceName) ? new Uri(FOHealthData.ServiceName) : null,
                    FOHealthMetricValue = FOHealthData.Value,
                    RepairPolicy = new RepairPolicy(),
                };
            }

            protected override bool Check()
            {
                int count = Input.Arguments.Count;

                if (count == 1 && Input.Arguments[0].Value.GetValue().GetType() != typeof(TimeSpan))
                {
                    throw new GuanException(
                                "RestartVMPredicate: One optional argument is supported and it must be a TimeSpan " +
                                "(xx:yy:zz format, for example 00:30:00 represents 30 minutes).");
                }

                // Repair Policy
                repairConfiguration.RepairPolicy.RepairAction = RepairActionType.RestartVM;
                repairConfiguration.RepairPolicy.RepairId = FOHealthData.RepairId;
                repairConfiguration.RepairPolicy.TargetType = RepairTargetType.VirtualMachine;

                if (count == 1 && Input.Arguments[0].Value.GetValue() is TimeSpan)
                {
                    repairConfiguration.RepairPolicy.MaxTimePostRepairHealthCheck = (TimeSpan)Input.Arguments[0].Value.GetEffectiveTerm().GetValue();
                }

                // FH does not execute repairs for VM level mitigation. InfrastructureService (IS) does,
                // so, FH schedules VM repairs via RM and the execution is taken care of by IS (the executor).
                // Block attempts to create duplicate repair tasks.
                var repairTaskEngine = new RepairTaskEngine(RepairTaskManager.FabricClientInstance);
                var isRepairAlreadyInProgress =
                    repairTaskEngine.IsFHRepairTaskRunningAsync(
                                        $"fabric:/System/InfrastructureService/{FOHealthData.NodeType}",
                                        repairConfiguration,
                                        RepairTaskManager.Token).GetAwaiter().GetResult();
                
                if (isRepairAlreadyInProgress)
                {
                    string message = $"VM Repair {FOHealthData.RepairId} is already in progress. Will not attempt repair at this time.";

                    RepairTaskManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                            LogLevel.Info,
                                                            $"RestartVMPredicateType::{FOHealthData.RepairId}",
                                                            message,
                                                            RepairTaskManager.Token).GetAwaiter().GetResult();

                    return false;
                }

                bool success = FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                        () =>
                                                            RepairTaskManager.ExecuteRMInfrastructureRepairTask(
                                                                                repairConfiguration,
                                                                                RepairTaskManager.Token),
                                                        RepairTaskManager.Token).ConfigureAwait(false).GetAwaiter().GetResult();

                return success;
            }
        }

        public static RestartVMPredicateType Singleton(string name, RepairTaskManager repairTaskManager, TelemetryData foHealthData)
        {
            RepairTaskManager = repairTaskManager;
            FOHealthData = foHealthData;

            return Instance ??= new RestartVMPredicateType(name);
        }

        private RestartVMPredicateType(string name)
                 : base(name, true, 0, 1)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}
