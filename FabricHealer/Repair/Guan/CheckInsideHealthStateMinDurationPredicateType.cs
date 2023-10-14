﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Threading.Tasks;
using Guan.Logic;
using FabricHealer.Utilities.Telemetry;

namespace FabricHealer.Repair.Guan
{
    public class CheckInsideHealthStateMinDurationPredicateType : PredicateType
    {
        private static TelemetryData RepairData;
        private static CheckInsideHealthStateMinDurationPredicateType Instance;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

            }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
            protected override async Task<bool> CheckAsync()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
            {
                TimeSpan timeWindow, duration;

                if (Input.Arguments.Count == 0 || Input.Arguments[0].Value.GetEffectiveTerm().GetObjectValue().GetType() != typeof(TimeSpan))
                {
                    throw new GuanException(
                                "CheckInsideHealthStateMinDuration: One argument is required and it must be a TimeSpan " +
                                "(xx:yy:zz format, for example 00:30:00 represents 30 minutes).");
                }

                timeWindow = (TimeSpan)Input.Arguments[0].Value.GetEffectiveTerm().GetObjectValue();

                if (timeWindow == TimeSpan.MinValue || timeWindow == TimeSpan.Zero)
                {
                    return false;
                }

                duration = RepairTaskManager.GetEntityCurrentHealthStateDuration(RepairData);

                if (duration <= timeWindow)
                {
                    return true;
                }

                return false;
            }
        }

        public static CheckInsideHealthStateMinDurationPredicateType Singleton(string name, TelemetryData repairData)
        {
            RepairData = repairData;
            return Instance ??= new CheckInsideHealthStateMinDurationPredicateType(name);
        }

        private CheckInsideHealthStateMinDurationPredicateType(string name)
                 : base(name, true, 1)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}