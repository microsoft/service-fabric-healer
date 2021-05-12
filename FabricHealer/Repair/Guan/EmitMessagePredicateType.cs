// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Globalization;
using Guan.Common;
using Guan.Logic;
using FabricHealer.Utilities;

namespace FabricHealer.Repair.Guan
{
    /// <summary>
    ///  Helper external predicate that generates health/etw/telemetry events.
    /// </summary>
    public class EmitMessagePredicateType : PredicateType
    {
        private static EmitMessagePredicateType Instance;
        private static RepairTaskManager RepairTaskManager;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

            }

            protected override bool Check()
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
                    object[] args = new object[Input.Arguments.Count - 1];

                    for (int i = 1; i < Input.Arguments.Count; i++)
                    {
                        args[i - 1] = Input.Arguments[i].Value.GetEffectiveTerm();
                    }

                    output = string.Format(CultureInfo.InvariantCulture, format, args);
                }
                else
                {
                    output = format;
                }

                RepairTaskManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                        LogLevel.Info,
                                                        "EmitMessagePredicate",
                                                        output,
                                                        RepairTaskManager.Token).GetAwaiter().GetResult();
                return true;
            }
        }

        public static EmitMessagePredicateType Singleton(string name, RepairTaskManager repairTaskManager)
        {
            RepairTaskManager = repairTaskManager;

            return Instance ??= new EmitMessagePredicateType(name);
        }

        private EmitMessagePredicateType(string name)
                 : base(name, true, 1, 30)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}
