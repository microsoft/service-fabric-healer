﻿// ------------------------------------------------------------
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
    public class RestartFabricSystemProcessPredicateType : PredicateType
    {
        private static RepairTaskManager RepairTaskManager;
        private static TelemetryData FOHealthData;
        private static RestartFabricSystemProcessPredicateType Instance;

        private class Resolver : BooleanPredicateResolver
        {
            private readonly RepairConfiguration repairConfiguration;

            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

                repairConfiguration = new RepairConfiguration
                {
                    AppName = !string.IsNullOrWhiteSpace(FOHealthData.ApplicationName) ? new Uri(FOHealthData.ApplicationName) : null,
                    ContainerId = FOHealthData.ContainerId,
                    FOErrorCode = FOHealthData.Code,
                    NodeName = FOHealthData.NodeName,
                    NodeType = FOHealthData.NodeType,
                    PartitionId = !string.IsNullOrWhiteSpace(FOHealthData.PartitionId) ? new Guid(FOHealthData.PartitionId) : default,
                    ProcessId = FOHealthData.ProcessId > 0 ? FOHealthData.ProcessId : -1,
                    SystemServiceProcessName = !string.IsNullOrWhiteSpace(FOHealthData.SystemServiceProcessName) ? FOHealthData.SystemServiceProcessName : string.Empty,
                    ReplicaOrInstanceId = FOHealthData.ReplicaId > 0 ? FOHealthData.ReplicaId : 0,
                    ServiceName = !string.IsNullOrWhiteSpace(FOHealthData.ServiceName) ? new Uri(FOHealthData.ServiceName) : null,
                    FOHealthMetricValue = FOHealthData.Value,
                    RepairPolicy = new RepairPolicy()
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

                int count = Input.Arguments.Count;

                for (int i = 0; i < count; i++)
                {
                    var typeString = Input.Arguments[i].Value.GetValue().GetType().ToString();

                    switch (typeString)
                    {
                        case "System.TimeSpan":
                            repairConfiguration.RepairPolicy.MaxTimePostRepairHealthCheck = (TimeSpan)Input.Arguments[i].Value.GetEffectiveTerm().GetValue();
                            break;

                        case "System.Boolean":
                            repairConfiguration.RepairPolicy.DoHealthChecks = (bool)Input.Arguments[0].Value.GetEffectiveTerm().GetValue();
                            break;

                        default:
                            throw new GuanException($"Unsupported input: {Input.Arguments[i].Value.GetValue().GetType()}");
                    }
                }

                // Try to schedule repair with RM.
                var repairTask = FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                          () => RepairTaskManager.ScheduleFabricHealerRepairTaskAsync(
                                                                                    repairConfiguration,
                                                                                    RepairTaskManager.Token),
                                                           RepairTaskManager.Token).ConfigureAwait(false).GetAwaiter().GetResult();

                if (repairTask == null)
                {
                    return false;
                }

                // Try to execute repair (FH executor does this work and manages repair state).
                bool success = FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                        () => RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync(
                                                                                    repairTask,
                                                                                    repairConfiguration,
                                                                                    RepairTaskManager.Token),
                                                         RepairTaskManager.Token).ConfigureAwait(false).GetAwaiter().GetResult();
                return success;
            }
        }

        public static RestartFabricSystemProcessPredicateType Singleton(string name, RepairTaskManager repairTaskManager, TelemetryData foHealthData)
        {
            RepairTaskManager = repairTaskManager;
            FOHealthData = foHealthData;

            return Instance ??= new RestartFabricSystemProcessPredicateType(name);
        }

        private RestartFabricSystemProcessPredicateType(string name)
                 : base(name, true, 0, 2)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}

