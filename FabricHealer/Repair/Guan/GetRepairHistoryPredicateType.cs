// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Threading.Tasks;
using Guan.Logic;
using FabricHealer.Utilities.Telemetry;
using FabricHealer.Utilities;

namespace FabricHealer.Repair.Guan
{
    public class GetRepairHistoryPredicateType : PredicateType
    {
        private static RepairTaskManager RepairTaskManager;
        private static TelemetryData RepairData;
        private static GetRepairHistoryPredicateType Instance;

        private class Resolver : GroundPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context, 1)
            {

            }

            protected override async Task<Term> GetNextTermAsync()
            {
                long repairCount = 0;
                var timeWindow = (TimeSpan)Input.Arguments[1].Value.GetEffectiveTerm().GetObjectValue();

                if (timeWindow > TimeSpan.MinValue)
                {
                    repairCount = await FabricRepairTasks.GetCompletedRepairCountWithinTimeRangeAsync(
                                        timeWindow,
                                        RepairTaskManager.FabricClientInstance,
                                        RepairData,
                                        RepairTaskManager.Token);
                }
                else
                {
                    string message = 
                        "You must supply a valid TimeSpan string for TimeWindow argument of GetRepairHistoryPredicate. " +
                        "Default result has been supplied (0).";

                    await RepairTaskManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"GetRepairHistoryPredicate::{RepairData.RepairId}",
                            message,
                            RepairTaskManager.Token);
                }

                var result = new CompoundTerm(this.Input.Functor);
                result.AddArgument(new Constant(repairCount), "0");
                return result;
            }
        }

        public static GetRepairHistoryPredicateType Singleton(string name, RepairTaskManager repairTaskManager, TelemetryData repairData)
        {
            RepairTaskManager = repairTaskManager;
            RepairData = repairData;

            return Instance ??= new GetRepairHistoryPredicateType(name);
        }

        private GetRepairHistoryPredicateType(string name)
                 : base(name, true, 1)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }

        public override void AdjustTerm(CompoundTerm term, Rule rule)
        {
            if (term.Arguments[0].Value.IsGround())
            {
                throw new GuanException("The first argument, ?repairCount, of GetRepairHistoryPredicateType must be a variable: {0}", term);
            }
        }
    }
}
