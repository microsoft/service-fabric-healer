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
    public class CheckInsideNodeProbationPeriodPredicateType : PredicateType
    {
        private static CheckInsideNodeProbationPeriodPredicateType Instance;
        private static TelemetryData RepairData;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

            }

            protected override async Task<bool> CheckAsync()
            {
                TimeSpan interval;
                string machineRulesConfigFileName =
                        FabricHealerManager.GetSettingParameterValue(RepairConstants.MachineRepairPolicySectionName, RepairConstants.LogicRulesConfigurationFile);

                if (FabricHealerManager.CurrentlyExecutingLogicRulesFileName == machineRulesConfigFileName && !string.IsNullOrWhiteSpace(
                    FabricHealerManager.GetSettingParameterValue(RepairConstants.MachineRepairPolicySectionName, RepairConstants.NodeProbationPeriod)))
                {
                    _ = TimeSpan.TryParse(
                           FabricHealerManager.GetSettingParameterValue(RepairConstants.MachineRepairPolicySectionName, RepairConstants.NodeProbationPeriod),
                           out interval);
                }
                else if (Input.Arguments[0].Value.GetEffectiveTerm().GetObjectValue().GetType() == typeof(TimeSpan))
                {
                    interval = (TimeSpan)Input.Arguments[0].Value.GetEffectiveTerm().GetObjectValue();
                }
                else
                {
                    throw new GuanException(
                               "CheckInsideProbationPeriod: One argument is required and it must be a TimeSpan " +
                               "(xx:yy:zz format, for example 00:30:00 represents 30 minutes).");
                }

                bool insideProbationPeriod = 
                    await FabricRepairTasks.IsMachineInPostRepairProbationAsync(interval, RepairData.NodeName, FabricHealerManager.Token);

                if (!insideProbationPeriod)
                {
                    return false;
                }

                string message = $"Node {RepairData.NodeName} is currently in post-repair health probation ({interval}). " +
                                 $"Will not schedule another machine-level repair for {RepairData.NodeName} at this time.";

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"InsideNodeProbationPeriod::{RepairData.NodeName}",
                        message,
                        FabricHealerManager.Token);

                return true;
            }
        }

        public static CheckInsideNodeProbationPeriodPredicateType Singleton(string name, TelemetryData repairData)
        {
            RepairData = repairData;
            return Instance ??= new CheckInsideNodeProbationPeriodPredicateType(name);
        }

        private CheckInsideNodeProbationPeriodPredicateType(string name)
                 : base(name, true, 1)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}
