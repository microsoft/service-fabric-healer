// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Utilities.Telemetry;
using Guan.Logic;
using System;
using FabricHealer.Utilities;

namespace FabricHealer.Repair.Guan
{
    public class RestartFabricSystemProcessPredicateType : PredicateType
    {
        private static RepairTaskManager RepairTaskManager;
        private static TelemetryData FOHealthData;
        private static RestartFabricSystemProcessPredicateType Instance;

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
                    ContainerId = FOHealthData.ContainerId,
                    FOHealthCode = FOHealthData.Code,
                    NodeName = FOHealthData.NodeName,
                    NodeType = FOHealthData.NodeType,
                    PartitionId = !string.IsNullOrEmpty(FOHealthData.PartitionId) ? new Guid(FOHealthData.PartitionId) : default,
                    SystemServiceProcessName = !string.IsNullOrWhiteSpace(FOHealthData.SystemServiceProcessName) ? FOHealthData.SystemServiceProcessName : string.Empty,
                    ReplicaOrInstanceId = !string.IsNullOrEmpty(FOHealthData.ReplicaId) ? long.Parse(FOHealthData.ReplicaId) : default,
                    ServiceName = !string.IsNullOrEmpty(FOHealthData.ServiceName) ? new Uri(FOHealthData.ServiceName) : null,
                    FOHealthMetricValue = FOHealthData.Value,
                    RepairPolicy = new RepairPolicy(),
                };
            }

            protected override bool Check()
            {
                // Can only kill processes on the same node where the FH instance that took the job is running.
                if (repairConfiguration.NodeName != RepairTaskManager.Context.NodeContext.NodeName)
                {
                    return false;
                }

                // RepairPolicy
                repairConfiguration.RepairPolicy.RepairAction = RepairActionType.RestartProcess;
                repairConfiguration.RepairPolicy.RepairId = FOHealthData.RepairId;
                repairConfiguration.RepairPolicy.TargetType = RepairTargetType.Application;

                // Try to schedule repair with RM.
                var repairTask = FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                    () =>
                        RepairTaskManager.ScheduleFabricHealerRmRepairTaskAsync(
                            repairConfiguration,
                            RepairTaskManager.Token),
                    RepairTaskManager.Token).ConfigureAwait(true).GetAwaiter().GetResult();

                if (repairTask == null)
                {
                    return false;
                }

                // Try to execute repair (FH executor does this work and manages repair state).
                bool success = FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                    () =>
                    RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync(
                        repairTask,
                        repairConfiguration,
                        RepairTaskManager.Token),
                    RepairTaskManager.Token).ConfigureAwait(false).GetAwaiter().GetResult();

                return success;
            }
        }

        public static RestartFabricSystemProcessPredicateType Singleton(
            string name,
            RepairTaskManager repairTaskManager,
            TelemetryData foHealthData)
        {
            RepairTaskManager = repairTaskManager;
            FOHealthData = foHealthData;

            return Instance ??= new RestartFabricSystemProcessPredicateType(name);
        }

        private RestartFabricSystemProcessPredicateType(
            string name)
            : base(name, true, 0, 0)
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

