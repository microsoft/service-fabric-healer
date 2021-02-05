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
    public class RestartReplicaPredicateType : PredicateType
    {
        private static RepairTaskHelper RepairTaskHelper;
        private static TelemetryData FOHealthData;
        private static RestartReplicaPredicateType Instance;

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
                repairConfiguration.RepairPolicy.Id = FOHealthData.RepairId;
                repairConfiguration.RepairPolicy.TargetType = RepairTargetType.Application;
                
                // Try to schedule repair with RM.
                var repairTask = FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                    () =>
                        RepairTaskHelper.ScheduleFabricHealerRmRepairTaskAsync(
                            repairConfiguration,
                            RepairTaskHelper.Token),
                    RepairTaskHelper.Token).ConfigureAwait(true).GetAwaiter().GetResult();

                if (repairTask == null)
                {
                    return false;
                }

                // Try to execute custom repair (FH executor).
                bool success = FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                    () =>
                    RepairTaskHelper.ExecuteFabricHealerRmRepairTaskAsync(
                        repairTask,
                        repairConfiguration,
                        RepairTaskHelper.Token),
                    RepairTaskHelper.Token).ConfigureAwait(false).GetAwaiter().GetResult();

                return success;
            }
        }

        public static RestartReplicaPredicateType Singleton(
                        string name,
                        RepairTaskHelper repairTaskHelper,
                        TelemetryData foHealthData)
        {
            RepairTaskHelper = repairTaskHelper;
            FOHealthData = foHealthData;

            return Instance ??= new RestartReplicaPredicateType(name);
        }

        private RestartReplicaPredicateType(
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
