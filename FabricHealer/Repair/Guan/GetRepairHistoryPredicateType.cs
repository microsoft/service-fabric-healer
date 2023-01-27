// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Threading.Tasks;
using Guan.Logic;
using FabricHealer.Utilities.Telemetry;

namespace FabricHealer.Repair.Guan
{
    public class GetRepairHistoryPredicateType : PredicateType
    {
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
                long repairCount;
                TimeSpan timeWindow = TimeSpan.MinValue;
                long args = Input.Arguments.Count;

                for (int i = 1; i < args; i++)
                {
                    var typeString = Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue().GetType().Name;

                    switch (typeString)
                    {
                        case "TimeSpan":

                            timeWindow = (TimeSpan)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        // This only makes sense for Machine-level repair rules, where you can specify any string for machine repair action that is supported in your SF configuration.
                        // Otherwise, FH already knows what you mean with GetRepairHistory([TimeSpan value]), given the repair context (which FH creates).
                        case "String":

                            if (RepairData.EntityType == EntityType.Machine)
                            {
                                RepairData.RepairPolicy.RepairIdPrefix = RepairTaskEngine.InfraTaskIdPrefix;
                                RepairData.RepairPolicy.InfrastructureRepairName = Input.Arguments[i].Value.GetEffectiveTerm().GetStringValue();
                            }
                            break;

                        default:
                            throw new GuanException(
                                $"GetRepairHistoryPredicateType failure. Unsupported argument type: {Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue().GetType().Name}");
                    }
                }

                if (timeWindow > TimeSpan.MinValue)
                {
                    repairCount =
                        await FabricRepairTasks.GetCompletedFHRepairCountWithinTimeRangeAsync(timeWindow, RepairData, FabricHealerManager.Token);
                }
                else
                {
                    throw new GuanException("You must supply a valid TimeSpan string for TimeWindow argument of GetRepairHistoryPredicate.");
                }

                var result = new CompoundTerm(this.Input.Functor);
                result.AddArgument(new Constant(repairCount), "0");
                return result;
            }
        }

        public static GetRepairHistoryPredicateType Singleton(string name, TelemetryData repairData)
        {
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
