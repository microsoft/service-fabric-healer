// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Utilities.Telemetry;
using Guan.Logic;
using System;
using FabricHealer.Utilities;
using Guan.Common;

namespace FabricHealer.Repair.Guan
{
    public class RestartVMPredicateType : PredicateType
    {
        private static RepairTaskHelper RepairTaskHelper;
        private static TelemetryData FOHealthData;
        private static RestartVMPredicateType Instance;

        class Resolver : BooleanPredicateResolver
        {
            private readonly RepairConfiguration repairConfiguration;

            public Resolver(
                CompoundTerm input,
                Constraint constraint,
                QueryContext context)
                : base(input, constraint, context)
            {
                this.repairConfiguration = new RepairConfiguration
                {
                    AppName = !string.IsNullOrEmpty(FOHealthData.ApplicationName) ? new Uri(FOHealthData.ApplicationName) : null,
                    FOHealthCode = FOHealthData.Code,
                    NodeName = FOHealthData.NodeName,
                    NodeType = FOHealthData.NodeType,
                    PartitionId = !string.IsNullOrEmpty(FOHealthData.PartitionId) ? new Guid(FOHealthData.PartitionId) : default,
                    ReplicaOrInstanceId = !string.IsNullOrEmpty(FOHealthData.ReplicaId) ? long.Parse(FOHealthData.ReplicaId) : default,
                    ServiceName = !string.IsNullOrEmpty(FOHealthData.ServiceName) ? new Uri(FOHealthData.ServiceName) : null,
                    FOHealthMetricValue = FOHealthData.Value,
                    RepairPolicy = new RepairPolicy(),
                };
            }

            protected override bool Check()
            {
                long maxRepairCycles = 0;
                TimeSpan maxTimeWindow = TimeSpan.MinValue;
                TimeSpan runInterval = TimeSpan.MinValue;
                int count = Input.Arguments.Count;

                for (int i = 0; i < count; i++)
                {
                    // MaxRepairs=5, MaxTimeWindow=01:00:00...
                    switch (Input.Arguments[i].Name.ToLower())
                    {
                        case "maxrepairs":
                            maxRepairCycles = (long)Input.Arguments[i].Value.GetEffectiveTerm().GetValue();
                            break;

                        case "maxtimewindow":
                            maxTimeWindow = (TimeSpan)Input.Arguments[i].Value.GetEffectiveTerm().GetValue();
                            break;

                        default:
                            throw new GuanException($"Unsupported input: {Input.Arguments[i].Name}");
                    }
                }

                if (count == 2 && maxRepairCycles > 0 && maxTimeWindow > TimeSpan.MinValue)
                {
                    runInterval = TimeSpan.FromSeconds((long)maxTimeWindow.TotalSeconds / maxRepairCycles);
                }

                // Repair Policy
                this.repairConfiguration.RepairPolicy.CurrentAction = RepairAction.RestartVM;
                repairConfiguration.RepairPolicy.CycleTimeDistributionType = CycleTimeDistributionType.Even;
                repairConfiguration.RepairPolicy.Id = FOHealthData.RepairId;
                repairConfiguration.RepairPolicy.MaxRepairCycles = maxRepairCycles;
                repairConfiguration.RepairPolicy.RepairCycleTimeWindow = maxTimeWindow;
                repairConfiguration.RepairPolicy.TargetType = RepairTargetType.VirtualMachine;
                repairConfiguration.RepairPolicy.RunInterval = runInterval;

                // FH does not execute repairs for VM level mitigation. InfrastructureService (IS) does,
                // so, FH schedules VM repairs via RM and the execution is taken care of by IS (the executor).
                // Block attempts to create duplicate repair tasks.
                var repairTaskEngine = new RepairTaskEngine(RepairTaskHelper.FabricClientInstance);
                var isRepairAlreadyInProgress =
                    repairTaskEngine.IsFHRepairTaskRunningAsync(
                        $"fabric:/System/InfrastructureService/{FOHealthData.NodeType}",
                        repairConfiguration,
                        RepairTaskHelper.Token).GetAwaiter().GetResult();
                
                if (isRepairAlreadyInProgress)
                {
                    string message =
                    $"VM Repair {FOHealthData.RepairId} is already in progress. Will not attempt repair at this time.";

                    RepairTaskHelper.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"RestartVMPredicateType::{FOHealthData.RepairId}",
                        message,
                        RepairTaskHelper.Token).GetAwaiter().GetResult();

                    return false;
                }

                bool success = FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                            () =>
                            RepairTaskHelper.ExecuteRMInfrastructureRepairTask(
                                repairConfiguration,
                                RepairTaskHelper.CompletedVmRepairs,
                                RepairTaskHelper.Token),
                            RepairTaskHelper.Token).ConfigureAwait(false).GetAwaiter().GetResult();

                return success;
            }
        }

        public static RestartVMPredicateType Singleton(
                    string name,
                    RepairTaskHelper repairTaskHelper,
                    TelemetryData foHealthData)
        {
            RepairTaskHelper = repairTaskHelper;
            FOHealthData = foHealthData;

            return Instance ??= new RestartVMPredicateType(name);
        }

        private RestartVMPredicateType(
            string name)
            : base(name, true, 0, 2)
        {

        }

        public override PredicateResolver CreateResolver(
            CompoundTerm input,
            Constraint constraint,
            QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}
