﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using Guan.Logic;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using System.Threading.Tasks;
using System.Threading;

namespace FabricHealer.Repair.Guan
{
    public class RestartFabricSystemProcessPredicateType : PredicateType
    {
        private static TelemetryData RepairData;
        private static RestartFabricSystemProcessPredicateType Instance;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

            }

            protected override async Task<bool> CheckAsync()
            {
                // Can only kill processes on the same node where the FH instance that took the job is running.
                if (RepairData.NodeName != FabricHealerManager.ServiceContext.NodeContext.NodeName)
                {
                    return false;
                }

                RepairData.RepairPolicy.RepairAction = RepairActionType.RestartProcess;

                if (FabricHealerManager.ConfigSettings.EnableLogicRuleTracing)
                {
                    _ = await RepairTaskEngine.TryTraceCurrentlyExecutingRuleAsync(Input.ToString(), RepairData, FabricHealerManager.Token);
                }

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
                            throw new GuanException($"Unsupported argument type for RestartFabricSystemProcess: {typeString}");
                    }
                }

                // Try to schedule repair with RM.
                var repairTask = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () => RepairTaskManager.ScheduleFabricHealerRepairTaskAsync(
                                                RepairData,
                                                FabricHealerManager.Token),
                                        FabricHealerManager.Token);
                if (repairTask == null)
                {
                    return false;
                }

                // MaxExecutionTime impl.
                using (CancellationTokenSource tokenSource = new())
                {
                    using (var linkedCTS = CancellationTokenSource.CreateLinkedTokenSource(
                                                                    tokenSource.Token,
                                                                    FabricHealerManager.Token))
                    {

                        TimeSpan maxExecutionTime = TimeSpan.FromMinutes(30);

                        if (RepairData.RepairPolicy.MaxExecutionTime > TimeSpan.Zero)
                        {
                            maxExecutionTime = RepairData.RepairPolicy.MaxExecutionTime;
                        }

                        tokenSource.CancelAfter(maxExecutionTime);
                        tokenSource.Token.Register(() =>
                        {
                            _ = FabricHealerManager.TryCleanUpOrphanedFabricHealerRepairJobsAsync();
                        });

                        bool success = false;

                        try
                        {
                            success = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                () => RepairTaskManager.ExecuteFabricHealerRepairTaskAsync(
                                                        repairTask,
                                                        RepairData,
                                                        linkedCTS.Token),
                                                linkedCTS.Token);
                        }
                        catch (Exception e) when (e is not OutOfMemoryException)
                        {
                            if (e is not TaskCanceledException and not OperationCanceledException)
                            {
                                string message = $"Failed to execute {RepairData.RepairPolicy.RepairAction} for repair {RepairData.RepairPolicy.RepairId}: {e.Message}";
#if DEBUG
                                message += $"{Environment.NewLine}{e}";
#endif
                                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "RestartFabricSystemProcessPredicateType::HandledException",
                                        message,
                                        FabricHealerManager.Token);
                            }

                            success = false;
                        }

                        if (!success && linkedCTS.IsCancellationRequested)
                        {
                            await FabricHealerManager.TryCleanUpOrphanedFabricHealerRepairJobsAsync();
                            return true;
                        }

                        return success;
                    }
                }
            }
        }

        public static RestartFabricSystemProcessPredicateType Singleton(string name, TelemetryData repairData)
        {
            RepairData = repairData;
            return Instance ??= new RestartFabricSystemProcessPredicateType(name);
        }

        private RestartFabricSystemProcessPredicateType(string name)
                 : base(name, true, 0)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}

