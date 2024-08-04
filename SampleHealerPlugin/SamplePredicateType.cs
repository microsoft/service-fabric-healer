using System.Fabric;
using System.Globalization;
using FabricHealer.Interfaces;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using Guan.Logic;
using Polly;
using Polly.Retry;

namespace FabricHealer.SamplePlugins
{
    /// <summary>
    ///  sample external predicate. copy of LogWarningPredicateType.
    ///  Implemented using the new Fabric healer plugin contracts.
    /// </summary>
    public class SamplePredicateType : PredicateType, IPredicate
    {
        private static SampleTelemetryData? _repairData;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

            }

            protected override async Task<bool> CheckAsync()
            {
                int count = this.Input.Arguments.Count;
                string output;

                if (count == 0)
                {
                    throw new GuanException("SamplePredicateType: At least 1 argument is required.");
                }

                var format = this.Input.Arguments[0].Value.GetEffectiveTerm().GetStringValue();

                if (string.IsNullOrWhiteSpace(format))
                {
                    return true;
                }

                // formatted args string?
                if (count > 1)
                {
                    object[] args = new object[count - 1];

                    for (int i = 1; i < count; i++)
                    {
                        args[i - 1] = this.Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                    }

                    output = string.Format(CultureInfo.InvariantCulture, format, args);
                }
                else
                {
                    output = format;
                }

                if (JsonSerializationUtility.TrySerializeObject(SamplePredicateType._repairData!, out string customTelemetry))
                {
                    output += " | additional telemetry info - " + customTelemetry;
                }

                AsyncRetryPolicy retryPolicy = Policy.Handle<FabricException>()
                                   .Or<TimeoutException>()
                                   .WaitAndRetryAsync(
                                       new[]
                                       {
                                            TimeSpan.FromSeconds(1),
                                            TimeSpan.FromSeconds(3),
                                            TimeSpan.FromSeconds(5),
                                            TimeSpan.FromSeconds(10),
                                       });
                await retryPolicy.ExecuteAsync(
                        () => FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "SamplePredicateType",
                        output,
                        FabricHealerManager.Token));

                return true;
            }
        }

        public SamplePredicateType(string name)
                 : base(name, true, 1)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }

        public void SetRepairData<T>(T repairData) where T : TelemetryData
        {
            SamplePredicateType._repairData = repairData as SampleTelemetryData;
        }
    }
}

