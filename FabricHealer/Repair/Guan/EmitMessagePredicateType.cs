﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Globalization;
using Guan.Logic;
using FabricHealer.Utilities;
using System.Threading.Tasks;

namespace FabricHealer.Repair.Guan
{
    /// <summary>
    ///  Helper external predicate that generates health/etw/telemetry events.
    /// </summary>
    public class EmitMessagePredicateType : PredicateType
    {
        private static EmitMessagePredicateType Instance;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

            }

            protected override async Task<bool> CheckAsync()
            {
                int count = Input.Arguments.Count;
                string output;

                if (count == 0)
                {
                    throw new GuanException("At least one argument is required.");
                }

                string format = Input.Arguments[0].Value.GetEffectiveTerm().GetStringValue();

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

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "EmitMessagePredicate",
                        output,
                        FabricHealerManager.Token);

                return true;
            }
        }

        public static EmitMessagePredicateType Singleton(string name)
        {
            return Instance ??= new EmitMessagePredicateType(name);
        }

        private EmitMessagePredicateType(string name)
                 : base(name, true, 1)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}
