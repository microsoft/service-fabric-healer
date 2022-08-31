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
    public class GetCurrentEntityHealthStateDurationPredicateType : PredicateType
    {
        private static RepairTaskManager RepairTaskManager;
        private static GetCurrentEntityHealthStateDurationPredicateType Instance;

        private class Resolver : GroundPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context, 1)
            {

            }

            // GetCurrentEntityHealthStateDuration(?HealthStateDuration, Machine, ?NodeName, State=Error)
            protected override async Task<Term> GetNextTermAsync()
            {
                if (Input.Arguments.Count != 4)
                {
                    throw new GuanException("GetCurrentEntityHealthStateDuration predicate requires 4 arguments.");
                }

                TimeSpan duration;

                if (!Enum.TryParse((string)Input.Arguments[1].Value.GetEffectiveTerm().GetObjectValue(), out EntityType entityType))
                {
                    throw new GuanException("The second argument of GetCurrentEntityHealthStateDuration must be a valid EntityType value (Application, Service, Node, Machine, etc..)");
                }

                if (!Enum.TryParse((string)Input.Arguments[3].Value.GetEffectiveTerm().GetObjectValue(), out HealthState state))
                {
                    throw new GuanException("The third argument of GetCurrentEntityHealthStateDuration must be a valid HealthState value (Error, Warning, etc..)");
                }

                string nodeName = (string)Input.Arguments[2].Value.GetEffectiveTerm().GetObjectValue();
                
                
                duration = await FabricRepairTasks.GetEntityCurrentHealthStateDurationAsync(entityType, nodeName, state, RepairTaskManager.Token);

                var result = new CompoundTerm(this.Input.Functor);
                result.AddArgument(new Constant(duration), "0");
                return result;
            }
        }

        public static GetCurrentEntityHealthStateDurationPredicateType Singleton(string name, RepairTaskManager repairTaskManager)
        {
            RepairTaskManager = repairTaskManager;
            return Instance ??= new GetCurrentEntityHealthStateDurationPredicateType(name);
        }

        private GetCurrentEntityHealthStateDurationPredicateType(string name)
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
