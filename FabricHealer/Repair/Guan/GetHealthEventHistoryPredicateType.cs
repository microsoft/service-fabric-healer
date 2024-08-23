﻿// ------------------------------------------------------------
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
    public class GetHealthEventHistoryPredicateType : PredicateType
    {
        private static TelemetryData RepairData;
        private static GetHealthEventHistoryPredicateType Instance;

        private class Resolver : GroundPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context, 1)
            {

            }

            protected override async Task<Term> GetNextTermAsync()
            {
                long eventCount = 0;
                var timeRange = (TimeSpan)Input.Arguments[1].Value.GetObjectValue();

                if (timeRange > TimeSpan.MinValue)
                {
                    eventCount = RepairTaskManager.GetEntityHealthEventCountWithinTimeRange(RepairData, timeRange);
                }
                else
                {
                    string message = "You must supply a valid TimeSpan argument for GetHealthEventHistoryPredicateType. " +
                                     "Default result has been supplied (0).";

                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"GetHealthEventHistoryPredicateType::{RepairData.Property}",
                            message,
                            FabricHealerManager.Token);
                }

                var result = new CompoundTerm(Input.Functor);
                result.AddArgument(new Constant(eventCount), "0");
                return result;
            }
        }

        public static GetHealthEventHistoryPredicateType Singleton(string name, TelemetryData repairData)
        {
            RepairData = repairData;
            return Instance ??= new GetHealthEventHistoryPredicateType(name);
        }

        private GetHealthEventHistoryPredicateType(string name)
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
                throw new GuanException("The first argument of GetHealthEventHistory must be a variable: {0}", term);
            }
        }
    }
}
