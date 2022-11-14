// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Threading.Tasks;
using Guan.Logic;
using FabricHealer.Utilities.Telemetry;
using System.Fabric.Health;

namespace FabricHealer.Repair.Guan
{
    public class GetEntityHealthStateDurationPredicateType : PredicateType
    {
        private static TelemetryData RepairData;
        private static GetEntityHealthStateDurationPredicateType Instance;
        private static RepairTaskManager RepairTaskManager;

        private class Resolver : GroundPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context, 1)
            {

            }

            protected override async Task<Term> GetNextTermAsync()
            {
                TimeSpan duration;
                duration = await RepairTaskManager.GetEntityCurrentHealthStateDurationAsync(RepairData, FabricHealerManager.Token);

                var result = new CompoundTerm(Input.Functor);
                result.AddArgument(new Constant(duration), "0");
                return result;
            }
        }

        public static GetEntityHealthStateDurationPredicateType Singleton(string name, TelemetryData repairData, RepairTaskManager repairTaskManager)
        {
            RepairData = repairData;
            RepairTaskManager = repairTaskManager;
            return Instance ??= new GetEntityHealthStateDurationPredicateType(name);
        }

        private GetEntityHealthStateDurationPredicateType(string name)
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
                throw new GuanException("The first argument of GetCurrentEntityHealthStateDuration must be a variable: {0}", term);
            }
        }
    }
}
