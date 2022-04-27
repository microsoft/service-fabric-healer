// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric.Repair;
using Guan.Logic;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using System.Threading.Tasks;

namespace FabricHealer.Repair.Guan
{
    public class RestartFabricNodePredicateType : PredicateType
    {
        private static RepairTaskManager RepairTaskManager;
        private static RepairExecutorData RepairExecutorData;
        private static RepairTaskEngine RepairTaskEngine;
        private static TelemetryData RepairData;
        private static RestartFabricNodePredicateType Instance;

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
                    PartitionId = default,
                    ReplicaOrInstanceId = RepairData.ReplicaId > 0 ? RepairData.ReplicaId : 0,
                    ServiceName = (!string.IsNullOrWhiteSpace(RepairData.ServiceName) && RepairData.ServiceName.Contains("fabric:/")) ? new Uri(RepairData.ServiceName) : null,
                    MetricValue = RepairData.Value,
                    RepairPolicy = new RepairPolicy
                    {
                        RepairAction = RepairActionType.RestartFabricNode,
                        RepairId = RepairData.RepairId
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

                RepairTask repairTask;
                bool success;

                // This means it's a resumed repair.
                if (RepairExecutorData != null)
                {
                    // Historical info, like what step the healer was in when the node went down, is contained in the
                    // executordata instance.
                    repairTask = await RepairTaskEngine.CreateFabricHealerRepairTask(RepairExecutorData, RepairTaskManager.Token);
                    success = await RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync(
                                        repairTask,
                                        repairConfiguration,
                                        RepairTaskManager.Token);
                    return success;
                }

                // Block attempts to create node-level repair tasks if one is already running in the cluster.
                var repairTaskEngine = new RepairTaskEngine(RepairTaskManager.FabricClientInstance);
                var isNodeRepairAlreadyInProgress =
                    await repairTaskEngine.IsFHRepairTaskRunningAsync(
                            RepairTaskEngine.FabricHealerExecutorName,
                            repairConfiguration,
                            RepairTaskManager.Token);

                if (isNodeRepairAlreadyInProgress)
                {
                    string message =
                    $"A Fabric Node repair, {RepairData.RepairId}, is already in progress in the cluster. Will not attempt repair at this time.";

                    await RepairTaskManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"RestartFabricNodePredicateType::{RepairData.RepairId}",
                            message,
                            RepairTaskManager.Token);

                    return false;
                }

                // Try to schedule repair with RM.
                repairTask = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                    () => RepairTaskManager.ScheduleFabricHealerRepairTaskAsync(
                                            repairConfiguration,
                                            RepairTaskManager.Token),
                                    RepairTaskManager.Token);

                if (repairTask == null)
                {
                    return false;
                }

                // Try to execute custom repair (FH executor).
                success = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                    () => RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync(
                                            repairTask,
                                            repairConfiguration,
                                            RepairTaskManager.Token),
                                    RepairTaskManager.Token);
                return success;
            }
        }

        public static RestartFabricNodePredicateType Singleton(
                        string name,
                        RepairTaskManager repairTaskManager,
                        RepairExecutorData repairExecutorData,
                        RepairTaskEngine repairTaskEngine,
                        TelemetryData repairData)
        {
            RepairTaskManager = repairTaskManager;
            RepairExecutorData = repairExecutorData;
            RepairTaskEngine = repairTaskEngine;
            RepairData = repairData;
            
            return Instance ??= new RestartFabricNodePredicateType(name);
        }

        private RestartFabricNodePredicateType(string name)
                 : base(name, true, 0)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}
