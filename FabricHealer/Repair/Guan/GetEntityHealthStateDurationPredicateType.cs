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

        private class Resolver : GroundPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context, 1)
            {

            }

            // GetEntityHealthStateDuration(?HealthStateDuration, Machine, State=Error)
            protected override async Task<Term> GetNextTermAsync()
            {
                if (Input.Arguments.Count != 3)
                {
                    throw new GuanException("GetCurrentEntityHealthStateDuration predicate requires 3 arguments.");
                }

                TimeSpan duration;

                if (!Enum.TryParse((string)Input.Arguments[1].Value.GetEffectiveTerm().GetObjectValue(), out EntityType entityType))
                {
                    throw new GuanException("The second argument of GetCurrentEntityHealthStateDuration must be a valid EntityType value (Application, Service, Node, Machine, etc..)");
                }

                if (!Enum.TryParse((string)Input.Arguments[2].Value.GetEffectiveTerm().GetObjectValue(), out HealthState state))
                {
                    throw new GuanException("The third argument of GetCurrentEntityHealthStateDuration must be a valid HealthState value (Error, Warning, etc..)");
                }

                switch (entityType)
                {
                    case EntityType.Application:
                        duration = await FabricRepairTasks.GetEntityCurrentHealthStateDurationAsync(entityType, RepairData.ApplicationName, state, FabricHealerManager.Token);
                        break;

                    case EntityType.Disk:
                    case EntityType.Machine:
                    case EntityType.Node:

                        duration = await FabricRepairTasks.GetEntityCurrentHealthStateDurationAsync(entityType, RepairData.NodeName, state, FabricHealerManager.Token);
                        break;

                    case EntityType.Partition:
                        duration = await FabricRepairTasks.GetEntityCurrentHealthStateDurationAsync(entityType, RepairData.PartitionId.ToString(), state, FabricHealerManager.Token);
                        break;

                    case EntityType.Service:
                        duration = await FabricRepairTasks.GetEntityCurrentHealthStateDurationAsync(entityType, RepairData.ServiceName, state, FabricHealerManager.Token);
                        break;

                    default:
                        throw new GuanException($"Unsupported entity type: {entityType}");
                }

                var result = new CompoundTerm(this.Input.Functor);
                result.AddArgument(new Constant(duration), "0");
                return result;
            }
        }

        public static GetEntityHealthStateDurationPredicateType Singleton(string name, TelemetryData repairData)
        {
            RepairData = repairData;
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
