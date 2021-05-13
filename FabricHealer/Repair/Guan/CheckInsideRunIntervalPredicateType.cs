// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using Guan.Common;
using Guan.Logic;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;

namespace FabricHealer.Repair.Guan
{
    public class CheckInsideRunIntervalPredicateType : PredicateType
    {
        private static CheckInsideRunIntervalPredicateType Instance;
        private static RepairTaskManager RepairTaskManager;
        private static TelemetryData FOHealthData;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

            }

            protected override bool Check()
            {
                TimeSpan runInterval = TimeSpan.MinValue;
                int count = Input.Arguments.Count;

                if (count == 0 || Input.Arguments[0].Value.GetValue().GetType() != typeof(TimeSpan))
                {
                    throw new GuanException(
                                "CheckInsideRunInterval: One argument is required and it must be a TimeSpan " +
                                "(xx:yy:zz format, for example 00:30:00 represents 30 minutes).");
                }

                var interval = (TimeSpan)Input.Arguments[0].Value.GetEffectiveTerm().GetValue();

                if (interval == TimeSpan.MinValue)
                {
                    return false;
                }
               
                bool insideRunInterval = FabricRepairTasks.IsLastCompletedFHRepairTaskWithinTimeRangeAsync(
                                                             interval,
                                                             RepairTaskManager.FabricClientInstance,
                                                             FOHealthData,
                                                             RepairTaskManager.Token).GetAwaiter().GetResult();
                
                if (!insideRunInterval)
                {
                    return false;
                }

                string message = $"Repair with ID {FOHealthData.RepairId} has already run once within the specified run interval ({(runInterval > TimeSpan.MinValue ? runInterval : interval)}).{Environment.NewLine}" +
                                 "Will not attempt repair at this time.";

                RepairTaskManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                         LogLevel.Info,
                                                         $"CheckInsideRunInterval::{FOHealthData.RepairId}",
                                                         message,
                                                         RepairTaskManager.Token).GetAwaiter().GetResult();
                return true;
            }
        }

        public static CheckInsideRunIntervalPredicateType Singleton(string name, RepairTaskManager repairTaskManager, TelemetryData foHealthData)
        {
            RepairTaskManager = repairTaskManager;
            FOHealthData = foHealthData;

            return Instance ??= new CheckInsideRunIntervalPredicateType(name);
        }

        private CheckInsideRunIntervalPredicateType(string name)
                 : base(name, true, 1, 3)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}
