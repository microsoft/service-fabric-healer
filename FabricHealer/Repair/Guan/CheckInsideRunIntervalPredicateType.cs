// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using Guan.Common;
using Guan.Logic;

namespace FabricHealer.Repair.Guan
{
    public class CheckInsideRunIntervalPredicateType : PredicateType
    {
        private static CheckInsideRunIntervalPredicateType Instance;
        private static RepairTaskHelper RepairTaskHelper;
        private static TelemetryData FOHealthData;

        class Resolver : BooleanPredicateResolver
        {
            public Resolver(
                CompoundTerm input,
                Constraint constraint,
                QueryContext context)
                : base(input, constraint, context)
            {

            }

            protected override bool Check()
            {
                TimeSpan runInterval = TimeSpan.MinValue;
                int count = Input.Arguments.Count;
                bool insideRunInterval = false;

                if (count == 0 || Input.Arguments[0].Name.ToLower() != "runinterval")
                {
                    throw new GuanException("RunInterval argument is required.");
                }

                TimeSpan interval = (TimeSpan)Input.Arguments[0].Value.GetEffectiveTerm().GetValue();

                // This means this repair hasn't been run at least once, so there is no data related to it in the repair 
                // manager state machine. lastRunTime is retrieved in GetRepairHistory predicate, provided to this predicate in related rules.
                if (interval > TimeSpan.MinValue)
                {
                    // Since FH is stateless -1, check for interval state outside of what is maintained in an FH instance state container.
                    insideRunInterval = FabricRepairTasks.IsLastCompletedFHRepairTaskWithinTimeRangeAsync(
                                            interval,
                                            RepairTaskHelper.FabricClientInstance,
                                            FOHealthData,
                                            RepairTaskHelper.Token).GetAwaiter().GetResult();
                }

                if (!insideRunInterval)
                {
                    return false;
                }

                string message =
                    $"Repair {FOHealthData.RepairId}:{FabricObserverErrorWarningCodes.GetMetricNameFromCode(FOHealthData.Code)} has already run once within the specified run interval." +
                    $"{Environment.NewLine}Run interval: {(runInterval > TimeSpan.MinValue ? runInterval : interval)}. Will not attempt repair at this time.";

                RepairTaskHelper.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    $"CheckRunIntervalPredicate::{FOHealthData.RepairId}",
                    message,
                    RepairTaskHelper.Token).GetAwaiter().GetResult();

                return insideRunInterval;
            }
        }

        public static CheckInsideRunIntervalPredicateType Singleton(
            string name,
            RepairTaskHelper repairTaskHelper,
            TelemetryData foHealthData)
        {
            RepairTaskHelper = repairTaskHelper;
            FOHealthData = foHealthData;

            return Instance ??= new CheckInsideRunIntervalPredicateType(name);
        }

        private CheckInsideRunIntervalPredicateType(
            string name)
            : base(name, true, 1, 3)
        {

        }

        public override PredicateResolver CreateResolver(
            CompoundTerm input,
            Constraint constraint,
            QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}
