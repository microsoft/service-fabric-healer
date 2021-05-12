// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric.Repair;
using Guan.Common;
using Guan.Logic;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;

namespace FabricHealer.Repair.Guan
{
    public class RestartFabricNodePredicateType : PredicateType
    {
        private static RepairTaskManager RepairTaskManager;
        private static RepairExecutorData RepairExecutorData;
        private static RepairTaskEngine RepairTaskEngine;
        private static TelemetryData FOHealthData;
        private static RestartFabricNodePredicateType Instance;

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
                    ServiceName = (!string.IsNullOrWhiteSpace(FOHealthData.ServiceName) && FOHealthData.ServiceName.Contains("fabric:/")) ? new Uri(FOHealthData.ServiceName) : null,
                    FOHealthMetricValue = FOHealthData.Value,
                    RepairPolicy = new RepairPolicy()
                };
            }

            protected override bool Check()
            {
                int count = Input.Arguments.Count;

                if (count == 1 && Input.Arguments[0].Value.GetValue().GetType() != typeof(TimeSpan))
                {
                    throw new GuanException(
                        "RestartFabricNodePredicate: One optional argument is supported and it must be a TimeSpan " +
                        "(xx:yy:zz format, for example 00:30:00 represents 30 minutes).");
                }

                RepairTask repairTask;

                // Repair Policy
                repairConfiguration.RepairPolicy.RepairAction = RepairActionType.RestartFabricNode;
                repairConfiguration.RepairPolicy.RepairId = FOHealthData.RepairId;
                repairConfiguration.RepairPolicy.TargetType = FOHealthData.ApplicationName == "fabric:/System" ? RepairTargetType.Application : RepairTargetType.Node;
                
                if (count == 1 && Input.Arguments[0].Value.GetValue() is TimeSpan)
                {
                    repairConfiguration.RepairPolicy.MaxTimePostRepairHealthCheck = (TimeSpan)Input.Arguments[0].Value.GetEffectiveTerm().GetValue();
                }

                bool success;

                // This means it's a resumed repair.
                if (RepairExecutorData != null)
                {
                    // Historical info, like what step the healer was in when the node went down, is contained in the
                    // executordata instance.
                    repairTask = RepairTaskEngine.CreateFabricHealerRmRepairTask(RepairExecutorData);

                    success = RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync(
                                                    repairTask,
                                                    repairConfiguration,
                                                    RepairTaskManager.Token).ConfigureAwait(false).GetAwaiter().GetResult();

                    return success;
                }

                // Block attempts to create node-level repair tasks if one is already running in the cluster.
                var repairTaskEngine = new RepairTaskEngine(RepairTaskManager.FabricClientInstance);
                var isNodeRepairAlreadyInProgress =
                    repairTaskEngine.IsFHRepairTaskRunningAsync(
                                        "FabricHealer",
                                        repairConfiguration,
                                        RepairTaskManager.Token).GetAwaiter().GetResult();

                if (isNodeRepairAlreadyInProgress)
                {
                    string message =
                    $"A Fabric Node repair, {FOHealthData.RepairId}, is already in progress in the cluster. Will not attempt repair at this time.";

                    RepairTaskManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                            LogLevel.Info,
                                                            $"RestartFabricNodePredicateType::{FOHealthData.RepairId}",
                                                            message,
                                                            RepairTaskManager.Token).GetAwaiter().GetResult();
                    return false;
                }

                // Try to schedule repair with RM.
                repairTask = FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                      () => RepairTaskManager.ScheduleFabricHealerRmRepairTaskAsync(
                                                                                repairConfiguration,
                                                                                RepairTaskManager.Token),
                                                       RepairTaskManager.Token).ConfigureAwait(true).GetAwaiter().GetResult();

                if (repairTask == null)
                {
                    return false;
                }

                // Try to execute custom repair (FH executor).
                success = FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                   () => RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync(
                                                                            repairTask,
                                                                            repairConfiguration,
                                                                            RepairTaskManager.Token),
                                                    RepairTaskManager.Token).ConfigureAwait(false).GetAwaiter().GetResult();
                return success;
            }
        }

        public static RestartFabricNodePredicateType Singleton(
                                                        string name,
                                                        RepairTaskManager repairTaskManager,
                                                        RepairExecutorData repairExecutorData,
                                                        RepairTaskEngine repairTaskEngine,
                                                        TelemetryData foHealthData)
        {
            RepairTaskManager = repairTaskManager;
            RepairExecutorData = repairExecutorData;
            RepairTaskEngine = repairTaskEngine;
            FOHealthData = foHealthData;
            
            return Instance ??= new RestartFabricNodePredicateType(name);
        }

        private RestartFabricNodePredicateType(string name)
                 : base(name, true, 0, 1)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}
