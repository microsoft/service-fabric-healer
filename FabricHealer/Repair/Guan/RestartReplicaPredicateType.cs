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
    public class RestartReplicaPredicateType : PredicateType
    {
        private static RepairTaskManager RepairTaskManager;
        private static TelemetryData RepairData;
        private static RestartReplicaPredicateType Instance;

        private class Resolver : BooleanPredicateResolver
        {
            private readonly RepairConfiguration repairConfiguration;

            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {
                repairConfiguration = new RepairConfiguration
                {
                    AppName = !string.IsNullOrWhiteSpace(RepairData.ApplicationName) ? new Uri(RepairData.ApplicationName) : null,
                    ErrorCode = RepairData.Code,
                    EntityType = RepairData.EntityType,
                    NodeName = RepairData.NodeName,
                    NodeType = RepairData.NodeType,
                    PartitionId = RepairData.PartitionId != Guid.Empty ? RepairData.PartitionId : default,
                    ReplicaOrInstanceId = RepairData.ReplicaId > 0 ? RepairData.ReplicaId : 0,
                    ServiceName = !string.IsNullOrWhiteSpace(RepairData.ServiceName) ? new Uri(RepairData.ServiceName) : null,
                    MetricValue = RepairData.Value,
                    RepairPolicy = new RepairPolicy
                    {
                        RepairId = RepairData.RepairId,
                        TargetType = RepairTargetType.Application,
                        RepairAction = RepairActionType.RestartReplica
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

                // Try to schedule repair with RM.
                var repairTask = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                          () => RepairTaskManager.ScheduleFabricHealerRepairTaskAsync(
                                                                                    repairConfiguration,
                                                                                    RepairTaskManager.Token),
                                                          RepairTaskManager.Token).ConfigureAwait(false);

                if (repairTask == null)
                {
                    return false;
                }

                // Try to execute custom repair (FH executor).
                bool success = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                        () => RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync(
                                                                                    repairTask,
                                                                                    repairConfiguration,
                                                                                    RepairTaskManager.Token),
                                                        RepairTaskManager.Token).ConfigureAwait(false);
                return success;
            }
        }

        public static RestartReplicaPredicateType Singleton(string name, RepairTaskManager repairTaskManager, TelemetryData repairData)
        {
            RepairTaskManager = repairTaskManager;
            RepairData = repairData;

            return Instance ??= new RestartReplicaPredicateType(name);
        }

        private RestartReplicaPredicateType(string name)
                 : base(name, true, 0)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}
