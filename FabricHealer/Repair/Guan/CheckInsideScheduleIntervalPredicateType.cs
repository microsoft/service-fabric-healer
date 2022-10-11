// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using Guan.Logic;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using System.Threading.Tasks;

namespace FabricHealer.Repair.Guan
{
    public class CheckInsideScheduleIntervalPredicateType : PredicateType
    {
        private static CheckInsideScheduleIntervalPredicateType Instance;
        private static TelemetryData RepairData;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

            }

            protected override async Task<bool> CheckAsync()
            {
                TimeSpan runInterval = TimeSpan.MinValue;
                int count = Input.Arguments.Count;

                if (count == 0 || Input.Arguments[0].Value.GetObjectValue().GetType() != typeof(TimeSpan))
                {
                    throw new GuanException(
                                "CheckInsideScheduleInterval: One argument is required and it must be a TimeSpan " +
                                "(xx:yy:zz format, for example 00:30:00 represents 30 minutes).");
                }

                var interval = (TimeSpan)Input.Arguments[0].Value.GetObjectValue();

                if (interval == TimeSpan.MinValue)
                {
                    return false;
                }

                bool insideRunInterval =
                    await FabricRepairTasks.IsLastScheduledRepairJobWithinTimeRangeAsync(
                            interval,
                            RepairData.EntityType == EntityType.Machine ? RepairConstants.InfrastructureServiceName : RepairConstants.FabricHealer,
                            FabricHealerManager.Token);

                if (!insideRunInterval)
                {
                    return false;
                }

                string message = $"{RepairData.RepairPolicy.RepairAction} job has already been scheduled at least once within the specified scheduling interval " +
                                 $"({(runInterval > TimeSpan.MinValue ? runInterval : interval)}).{Environment.NewLine}" +
                                 $"Will not schedule {RepairData.EntityType} repair at this time.";

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"CheckInsideScheduleInterval::{RepairData.RepairPolicy.RepairAction}",
                        message,
                        FabricHealerManager.Token);

                return true;
            }
        }

        public static CheckInsideScheduleIntervalPredicateType Singleton(string name, TelemetryData repairData)
        {
            RepairData = repairData;
            return Instance ??= new CheckInsideScheduleIntervalPredicateType(name);
        }

        private CheckInsideScheduleIntervalPredicateType(string name)
                 : base(name, true, 1)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}

