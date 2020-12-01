using FabricHealer.Utilities.Telemetry;
using Guan.Logic;
using System;
using FabricHealer.Utilities;
using Guan.Common;
using System.IO;

namespace FabricHealer.Repair.Guan
{
    public class DeleteFilesPredicateType : PredicateType
    {
        private static RepairTaskHelper RepairTaskHelper;
        private static TelemetryData FOHealthData;
        private static DeleteFilesPredicateType Instance;

        class Resolver : BooleanPredicateResolver
        {
            private readonly RepairConfiguration repairConfiguration;

            public Resolver(
                CompoundTerm input,
                Constraint constraint,
                QueryContext context)
                : base(input, constraint, context)
            {
                this.repairConfiguration = new RepairConfiguration
                {
                    AppName = !string.IsNullOrEmpty(FOHealthData.ApplicationName) ? new Uri(FOHealthData.ApplicationName) : null,
                    FOHealthCode = FOHealthData.Code,
                    NodeName = FOHealthData.NodeName,
                    NodeType = FOHealthData.NodeType,
                    PartitionId = !string.IsNullOrEmpty(FOHealthData.PartitionId) ? new Guid(FOHealthData.PartitionId) : default,
                    ReplicaOrInstanceId = !string.IsNullOrEmpty(FOHealthData.ReplicaId) ? long.Parse(FOHealthData.ReplicaId) : default,
                    ServiceName = !string.IsNullOrEmpty(FOHealthData.ServiceName) ? new Uri(FOHealthData.ServiceName) : null,
                    FOHealthMetricValue = FOHealthData.Value,
                    RepairPolicy = new RepairPolicy(),
                };
            }

            protected override bool Check()
            {
                string path = null;
                long maxRepairCycles = 0;
                TimeSpan maxTimeWindow = TimeSpan.MinValue;
                TimeSpan runInterval = TimeSpan.MinValue;
                int count = Input.Arguments.Count;

                for (int i = 0; i < count; i++)
                {
                    // MaxRepairs=5, MaxTimeWindow=01:00:00...
                    switch (Input.Arguments[i].Name.ToLower())
                    {
                        case "logpath":
                            path = (string)Input.Arguments[i].Value.GetEffectiveTerm().GetValue();
                            break;

                        case "maxrepairs":
                            maxRepairCycles = (long)Input.Arguments[i].Value.GetEffectiveTerm().GetValue();
                            break;

                        case "maxtimewindow":
                            maxTimeWindow = (TimeSpan)Input.Arguments[i].Value.GetEffectiveTerm().GetValue();
                            break;

                        default:
                            throw new GuanException($"Unsupported input: {Input.Arguments[i].Name}");
                    }
                }

                if (count == 3 && maxRepairCycles > 0 && maxTimeWindow > TimeSpan.MinValue)
                {
                    runInterval = TimeSpan.FromSeconds((long)maxTimeWindow.TotalSeconds / maxRepairCycles);
                }

                this.repairConfiguration.FolderPath = path;

                // RepairPolicy
                repairConfiguration.RepairPolicy.CurrentAction = RepairAction.DeleteFiles;
                repairConfiguration.RepairPolicy.CycleTimeDistributionType = CycleTimeDistributionType.Even;
                repairConfiguration.RepairPolicy.Id = FOHealthData.RepairId;
                repairConfiguration.RepairPolicy.MaxRepairCycles = maxRepairCycles;
                repairConfiguration.RepairPolicy.RepairCycleTimeWindow = maxTimeWindow;
                repairConfiguration.RepairPolicy.TargetType = RepairTargetType.VirtualMachine;
                repairConfiguration.RepairPolicy.RunInterval = runInterval;

                // Try to schedule repair with RM.
                var repairTask = FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                    () =>
                        RepairTaskHelper.ScheduleFabricHealerRmRepairTaskAsync(
                            repairConfiguration,
                            RepairTaskHelper.CompletedDiskRepairs,
                            RepairTaskHelper.Token),
                    RepairTaskHelper.Token).ConfigureAwait(true).GetAwaiter().GetResult();

                if (repairTask == null)
                {
                    return false;
                }

                // Try to execute repair (FH executor does this work and manages repair state).
                bool success = FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                    () =>
                    RepairTaskHelper.ExecuteFabricHealerRmRepairTaskAsync(
                        repairTask,
                        repairConfiguration,
                        RepairTaskHelper.CompletedDiskRepairs,
                        RepairTaskHelper.Token),
                    RepairTaskHelper.Token).ConfigureAwait(false).GetAwaiter().GetResult();

                return success;
            }
        }

        public static DeleteFilesPredicateType Singleton(
            string name,
            RepairTaskHelper repairTaskHelper,
            TelemetryData foHealthData)
        {
            FOHealthData = foHealthData;
            RepairTaskHelper = repairTaskHelper;

            return Instance ??= new DeleteFilesPredicateType(name);
        }

        private DeleteFilesPredicateType(
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
