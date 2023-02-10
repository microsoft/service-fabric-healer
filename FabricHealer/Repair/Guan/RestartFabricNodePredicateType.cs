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
using System.Threading;

namespace FabricHealer.Repair.Guan
{
    public class RestartFabricNodePredicateType : PredicateType
    {
        private static RepairExecutorData RepairExecutorData;
        private static TelemetryData RepairData;
        private static RestartFabricNodePredicateType Instance;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

            }

            protected override async Task<bool> CheckAsync()
            {
                RepairData.RepairPolicy.RepairAction = RepairActionType.RestartFabricNode;
                int count = Input.Arguments.Count;

                for (int i = 0; i < count; i++)
                {
                    var typeString = Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue().GetType().Name;

                    switch (typeString)
                    {
                        case "Boolean" when i == 0 && count == 3 || Input.Arguments[i].Name.ToLower() == "dohealthchecks":
                            RepairData.RepairPolicy.DoHealthChecks = (bool)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        case "TimeSpan" when i == 1 && count == 3 || Input.Arguments[i].Name.ToLower() == "maxwaittimeforhealthstateok":
                            RepairData.RepairPolicy.MaxTimePostRepairHealthCheck = (TimeSpan)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        case "TimeSpan" when i == 2 && count == 3 || Input.Arguments[i].Name.ToLower() == "maxexecutiontime":
                            RepairData.RepairPolicy.MaxExecutionTime = (TimeSpan)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        default:
                            throw new GuanException($"Unsupported argument type for RestartFabricNode: {typeString}");
                    }
                }

                RepairTask repairTask;
                bool success;

                // This means it's a resumed repair.
                if (RepairExecutorData != null)
                {
                    // Historical info, like what step the healer was in when the node went down, is contained in the
                    // executordata instance.
                    repairTask = await RepairTaskEngine.CreateFabricHealerRepairTask(RepairExecutorData, FabricHealerManager.Token);
                    
                    if (repairTask == null)
                    {
                        return false;
                    }

                    using (CancellationTokenSource tokenSource = new())
                    {
                        using (var linkedCTS = CancellationTokenSource.CreateLinkedTokenSource(
                                                                        tokenSource.Token,
                                                                        FabricHealerManager.Token))
                        {
                            TimeSpan maxExecutionTime = TimeSpan.FromMinutes(60);

                            if (RepairData.RepairPolicy.MaxExecutionTime > TimeSpan.Zero)
                            {
                                maxExecutionTime = RepairData.RepairPolicy.MaxExecutionTime;
                            }

                            tokenSource.CancelAfter(maxExecutionTime);
                            tokenSource.Token.Register(() =>
                            {
                                _ = FabricHealerManager.TryCleanUpOrphanedFabricHealerRepairJobsAsync();
                            });

                            // Try to execute repair (FH executor does this work and manages repair state).
                            success = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                    () => RepairTaskManager.ExecuteFabricHealerRepairTaskAsync(
                                                            repairTask,
                                                            RepairData,
                                                            linkedCTS.Token),
                                                    linkedCTS.Token);

                            if (!success && linkedCTS.IsCancellationRequested)
                            {
                                await FabricHealerManager.TryCleanUpOrphanedFabricHealerRepairJobsAsync();
                            }

                            return success;
                        }
                    }
                }
            
                // Block attempts to create node-level repair tasks if one is already running in the cluster.
                var isNodeRepairAlreadyInProgress =
                    await RepairTaskEngine.IsRepairInProgressAsync(RepairData, FabricHealerManager.Token);

                if (isNodeRepairAlreadyInProgress)
                {
                    string message =
                    $"A repair for Fabric node {RepairData.NodeName} is already in progress in the cluster. Will not node attempt repair at this time.";

                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"RestartFabricNode::{RepairData.RepairPolicy.RepairId}",
                            message,
                            FabricHealerManager.Token,
                            RepairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return false;
                }

                using (CancellationTokenSource tokenSource = new())
                {
                    using (var linkedCTS = CancellationTokenSource.CreateLinkedTokenSource(
                                                                    tokenSource.Token,
                                                                    FabricHealerManager.Token))
                    {
                        TimeSpan maxExecutionTime = TimeSpan.FromMinutes(60);

                        if (RepairData.RepairPolicy.MaxExecutionTime > TimeSpan.Zero)
                        {
                            maxExecutionTime = RepairData.RepairPolicy.MaxExecutionTime;
                        }

                        tokenSource.CancelAfter(maxExecutionTime);
                        tokenSource.Token.Register(() =>
                        {
                            _ = FabricHealerManager.TryCleanUpOrphanedFabricHealerRepairJobsAsync();
                        });

                        // Try to schedule repair with RM.
                        repairTask = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                () => RepairTaskManager.ScheduleFabricHealerRepairTaskAsync(
                                                        RepairData,
                                                        linkedCTS.Token),
                                                linkedCTS.Token);

                        if (repairTask == null)
                        {
                            return false;
                        }

                        // Try to execute repair (FH executor does this work and manages repair state).
                        success = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                () => RepairTaskManager.ExecuteFabricHealerRepairTaskAsync(
                                                        repairTask,
                                                        RepairData,
                                                        linkedCTS.Token),
                                                linkedCTS.Token);

                        if (!success && linkedCTS.IsCancellationRequested)
                        {
                            await FabricHealerManager.TryCleanUpOrphanedFabricHealerRepairJobsAsync();
                        }

                        return success;
                    }
                }
            }
        }

        public static RestartFabricNodePredicateType Singleton(
                        string name,
                        RepairExecutorData repairExecutorData,
                        TelemetryData repairData)
        {
            RepairExecutorData = repairExecutorData;
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