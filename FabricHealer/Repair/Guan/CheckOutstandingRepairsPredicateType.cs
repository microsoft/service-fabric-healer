// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Guan.Logic;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using System.Threading.Tasks;

namespace FabricHealer.Repair.Guan
{
    public class CheckOutstandingRepairsPredicateType : PredicateType
    {
        private static CheckOutstandingRepairsPredicateType Instance;
        private static TelemetryData RepairData;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

            }

            protected override async Task<bool> CheckAsync()
            {
                int count = Input.Arguments.Count;

                if (count == 0 || Input.Arguments[0].Value.GetEffectiveTerm().GetObjectValue().GetType() != typeof(long))
                {
                    throw new GuanException("CheckOutstandingRepairs: One argument is required and it must be a number.");
                }

                long maxRepairs = (long)Input.Arguments[0].Value.GetEffectiveTerm().GetObjectValue();
                
                if (maxRepairs == 0)
                {
                    return false;
                }

                RepairData.RepairPolicy.MaxConcurrentRepairs = maxRepairs;
                int outstandingRepairCount =
                   await RepairTaskEngine.GetAllOutstandingFHRepairsCountAsync(taskIdPrefix: RepairData.RepairPolicy.RepairIdPrefix, FabricHealerManager.Token);

                if (outstandingRepairCount >= maxRepairs)
                {
                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                           LogLevel.Info,
                           "CheckOutstandingRepairs",
                           $"Current number of outstanding machine repairs ({outstandingRepairCount}) >= Max ({maxRepairs}). Will not schedule another repair at this time.",
                           FabricHealerManager.Token,
                           null,
                           FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return true;
                }

                return false;
            }
        }

        public static CheckOutstandingRepairsPredicateType Singleton(string name, TelemetryData repairData)
        {
            RepairData = repairData;
            return Instance ??= new CheckOutstandingRepairsPredicateType(name);
        }

        private CheckOutstandingRepairsPredicateType(string name)
                 : base(name, true, 1)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}
