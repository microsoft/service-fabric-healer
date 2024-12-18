﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Globalization;
using Guan.Logic;
using FabricHealer.Utilities;
using Polly.Retry;
using Polly;
using System.Fabric;

namespace FabricHealer.SamplePlugins
{
    /// <summary>
    ///  sample external predicate. copy of LogWarningPredicateType.
    /// </summary>
    public class SampleRepairPredicateType : PredicateType
    {
        private static SampleRepairPredicateType Instance;
        private static SampleTelemetryData RepairData;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

            }

            protected override async Task<bool> CheckAsync()
            {
                int count = Input.Arguments.Count;
                string output, format;

                if (count == 0)
                {
                    throw new GuanException("SampleRepairPredicateType: At least 1 argument is required.");
                }

                format = Input.Arguments[0].Value.GetEffectiveTerm().GetStringValue();

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
                        args[i - 1] = Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                    }

                    output = string.Format(CultureInfo.InvariantCulture, format, args);
                }
                else
                {
                    output = format;
                }

                if (JsonSerializationUtility.TrySerializeObject(RepairData, out string customTelemetry))
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

        public static SampleRepairPredicateType Singleton(string name, SampleTelemetryData repairData)
        {
            RepairData = repairData;
            return Instance ??= new SampleRepairPredicateType(name);
        }

        private SampleRepairPredicateType(string name)
                 : base(name, true, 1)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}
