using System;
using Guan.Logic;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using System.Threading.Tasks;

namespace FabricHealer.Repair.Guan
{
    public class CheckInsideProbationPeriodType : PredicateType
    {
        private static CheckInsideProbationPeriodType Instance;
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

                if (count == 0 || Input.Arguments[0].Value.GetObjectValue().GetType() != typeof(TimeSpan))
                {
                    throw new GuanException(
                                "CheckInsideProbationPeriod: One argument is required and it must be a TimeSpan " +
                                "(xx:yy:zz format, for example 00:30:00 represents 30 minutes).");
                }

                var interval = (TimeSpan)Input.Arguments[0].Value.GetEffectiveTerm().GetObjectValue();

                if (interval == TimeSpan.MinValue)
                {
                    return false;
                }

                bool insideProbationPeriod =
                    await FabricRepairTasks.IsRepairInPostProbationAsync(
                            interval,
                            RepairData.EntityType == EntityType.Machine ? RepairTaskEngine.InfraTaskIdPrefix : RepairTaskEngine.FHTaskIdPrefix,
                            RepairData,
                            FabricHealerManager.Token);

                if (!insideProbationPeriod)
                {
                    return false;
                }

                string message = $"FH repair job {RepairData.RepairPolicy.RepairId} is currently in post-repair health probation ({interval}). " +
                                 $"Will not schedule another repair for the target {RepairData.RepairPolicy} at this time.";

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"CheckInsideProbationPeriod::{RepairData.RepairPolicy.RepairId}",
                        message,
                        FabricHealerManager.Token);

                return true;
            }
        }

        public static CheckInsideProbationPeriodType Singleton(string name, TelemetryData repairData)
        {
            RepairData = repairData;
            return Instance ??= new CheckInsideProbationPeriodType(name);
        }

        private CheckInsideProbationPeriodType(string name)
                 : base(name, true, 1)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}
