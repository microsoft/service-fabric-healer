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
    public class DeleteFilesPredicateType : PredicateType
    {
        private static RepairTaskManager RepairTaskManager;
        private static TelemetryData FOHealthData;
        private static DeleteFilesPredicateType Instance;

        private class Resolver : BooleanPredicateResolver
        {
            private readonly RepairConfiguration repairConfiguration;

            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {
                repairConfiguration = new RepairConfiguration
                {
                    AppName = !string.IsNullOrWhiteSpace(FOHealthData.ApplicationName) ? new Uri(FOHealthData.ApplicationName) : null,
                    FOErrorCode = FOHealthData.Code,
                    NodeName = FOHealthData.NodeName,
                    NodeType = FOHealthData.NodeType,
                    PartitionId = !string.IsNullOrWhiteSpace(FOHealthData.PartitionId) ? new Guid(FOHealthData.PartitionId) : default,
                    ReplicaOrInstanceId = FOHealthData.ReplicaId > 0 ? FOHealthData.ReplicaId : 0,
                    ServiceName = !string.IsNullOrWhiteSpace(FOHealthData.ServiceName) ? new Uri(FOHealthData.ServiceName) : null,
                    FOHealthMetricValue = FOHealthData.Value,
                    RepairPolicy = new DiskRepairPolicy()
                };
            }

            protected override bool Check()
            {
                // Can only delete files on the same VM where the FH instance that took the job is running.
                if (repairConfiguration.NodeName != RepairTaskManager.Context.NodeContext.NodeName)
                {
                    return false;
                }

                bool recurseSubDirectories = false;
                string path = null;

                // default as 0 means delete all files.
                long maxFilesToDelete = 0;
                FileSortOrder direction = FileSortOrder.Ascending;
                int count = Input.Arguments.Count;

                for (int i = 0; i < count; i++)
                {
                    switch (Input.Arguments[i].Name.ToLower())
                    {
                        case "sortorder":
                            direction = (FileSortOrder)Enum.Parse(typeof(FileSortOrder), (string)Input.Arguments[i].Value.GetEffectiveTerm().GetValue());
                            break;

                        case "folderpath":
                            path = (string)Input.Arguments[i].Value.GetEffectiveTerm().GetValue();
                            break;

                        case "maxfilestodelete":
                            maxFilesToDelete = (long)Input.Arguments[i].Value.GetEffectiveTerm().GetValue();
                            break;

                        case "recursesubdirectories":
                            recurseSubDirectories = bool.Parse((string)Input.Arguments[i].Value.GetEffectiveTerm().GetValue());
                            break;

                        default:
                            throw new GuanException($"Unsupported input: {Input.Arguments[i].Name}");
                    }
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new GuanException("Your DiskRepair logic rule must specify a FolderPath argument.");
                }

                // RepairPolicy
                repairConfiguration.RepairPolicy.RepairAction = RepairActionType.DeleteFiles;
                ((DiskRepairPolicy)repairConfiguration.RepairPolicy).FolderPath = path;
                repairConfiguration.RepairPolicy.RepairId = FOHealthData.RepairId;
                ((DiskRepairPolicy)repairConfiguration.RepairPolicy).MaxNumberOfFilesToDelete = maxFilesToDelete;
                ((DiskRepairPolicy)repairConfiguration.RepairPolicy).FileAgeSortOrder = direction;
                repairConfiguration.RepairPolicy.TargetType = RepairTargetType.VirtualMachine;
                ((DiskRepairPolicy)repairConfiguration.RepairPolicy).RecurseSubdirectories = recurseSubDirectories;

                // Try to schedule repair with RM.
                var repairTask = FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                          () => RepairTaskManager.ScheduleFabricHealerRepairTaskAsync(
                                                                                    repairConfiguration,
                                                                                    RepairTaskManager.Token),
                                                           RepairTaskManager.Token).ConfigureAwait(false).GetAwaiter().GetResult();

                if (repairTask == null)
                {
                    return false;
                }

                // Try to execute repair (FH executor does this work and manages repair state through RM, as always).
                bool success = FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                        () => RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync(
                                                                                    repairTask,
                                                                                    repairConfiguration,
                                                                                    RepairTaskManager.Token),
                                                         RepairTaskManager.Token).ConfigureAwait(false).GetAwaiter().GetResult();
                return success;
            }
        }

        public static DeleteFilesPredicateType Singleton(string name, RepairTaskManager repairTaskManager, TelemetryData foHealthData)
        {
            FOHealthData = foHealthData;
            RepairTaskManager = repairTaskManager;

            return Instance ??= new DeleteFilesPredicateType(name);
        }

        private DeleteFilesPredicateType(string name)
                 : base(name, true, 1, 4)
        {
           
        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}
