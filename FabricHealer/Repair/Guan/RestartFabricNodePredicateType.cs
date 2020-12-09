﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Utilities.Telemetry;
using Guan.Logic;
using System;
using System.Fabric.Repair;
using FabricHealer.Utilities;
using Guan.Common;

namespace FabricHealer.Repair.Guan
{
    public class RestartFabricNodePredicateType : PredicateType
    {
        private static RepairTaskHelper RepairTaskHelper;
        private static RepairExecutorData RepairExecutorData;
        private static RepairTaskEngine RepairTaskEngine;
        private static TelemetryData FOHealthData;
        private static RestartFabricNodePredicateType Instance;

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
                    ServiceName = (!string.IsNullOrEmpty(FOHealthData.ServiceName) && FOHealthData.ServiceName.Contains("fabric:/")) ? new Uri(FOHealthData.ServiceName) : null,
                    FOHealthMetricValue = FOHealthData.Value,
                    RepairPolicy = new RepairPolicy(),
                };
            }

            protected override bool Check()
            {
                RepairTask repairTask;
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
                repairConfiguration.RepairPolicy.CurrentAction = RepairAction.RestartFabricNode;
                repairConfiguration.RepairPolicy.CycleTimeDistributionType = CycleTimeDistributionType.Even;
                repairConfiguration.RepairPolicy.Id = FOHealthData.RepairId;
                repairConfiguration.RepairPolicy.MaxRepairCycles = maxRepairCycles;
                repairConfiguration.RepairPolicy.RepairCycleTimeWindow = maxTimeWindow;
                repairConfiguration.RepairPolicy.TargetType = FOHealthData.ApplicationName == "fabric:/System" ? RepairTargetType.Application : RepairTargetType.Node;
                repairConfiguration.RepairPolicy.RunInterval = runInterval;

                bool success;

                // This means it's a resumed repair.
                if (RepairExecutorData != null)
                {
                    // Historical info, like what step the healer was in when the node went down, is contained in the
                    // executordata instance.
                    repairTask = RepairTaskEngine.CreateFabricHealerRmRepairTask(this.repairConfiguration, RepairExecutorData);

                    success = RepairTaskHelper.ExecuteFabricHealerRmRepairTaskAsync(
                            repairTask,
                            this.repairConfiguration,
                            FOHealthData.ApplicationName == "fabric:/System" ? RepairTaskHelper.CompletedSystemAppRepairs : RepairTaskHelper.CompletedFabricNodeRepairs,
                            RepairTaskHelper.Token).ConfigureAwait(false).GetAwaiter().GetResult();

                    return success;
                }

                // Block attempts to create node-level repair tasks if one is already running in the cluster.
                var repairTaskEngine = new RepairTaskEngine(RepairTaskHelper.FabricClientInstance);
                var isNodeRepairAlreadyInProgress =
                    repairTaskEngine.IsFHRepairTaskRunningAsync(
                        $"FabricHealer",
                        repairConfiguration,
                        RepairTaskHelper.Token).GetAwaiter().GetResult();

                if (isNodeRepairAlreadyInProgress)
                {
                    string message =
                    $"A Fabric Node repair, {FOHealthData.RepairId}, is already in progress in the cluster. Will not attempt repair at this time.";

                    RepairTaskHelper.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"RestartFabricNodePredicateType::{FOHealthData.RepairId}",
                        message,
                        RepairTaskHelper.Token).GetAwaiter().GetResult();

                    return false;
                }

                // Try to schedule repair with RM.
                repairTask = FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                    () =>
                    RepairTaskHelper.ScheduleFabricHealerRmRepairTaskAsync(
                            this.repairConfiguration,
                            RepairTaskHelper.CompletedFabricNodeRepairs,
                            RepairTaskHelper.Token),
                        RepairTaskHelper.Token).ConfigureAwait(true).GetAwaiter().GetResult();

                if (repairTask == null)
                {
                    return false;
                }

                // Try to execute custom repair (FH executor).
                success = FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                    () =>
                    RepairTaskHelper.ExecuteFabricHealerRmRepairTaskAsync(
                            repairTask,
                            this.repairConfiguration,
                            RepairTaskHelper.CompletedFabricNodeRepairs,
                            RepairTaskHelper.Token),
                        RepairTaskHelper.Token).ConfigureAwait(false).GetAwaiter().GetResult();

                return success;
            }
        }

        public static RestartFabricNodePredicateType Singleton(
            string name,
            RepairTaskHelper repairTaskHelper,
            RepairExecutorData repairExecutorData,
            RepairTaskEngine repairTaskEngine,
            TelemetryData foHealthData)
        {
            RepairTaskHelper = repairTaskHelper;
            RepairExecutorData = repairExecutorData;
            RepairTaskEngine = repairTaskEngine;
            FOHealthData = foHealthData;
            
            return Instance ??= new RestartFabricNodePredicateType(name);
        }

        private RestartFabricNodePredicateType(
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