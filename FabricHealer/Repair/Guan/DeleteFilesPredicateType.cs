// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using Guan.Logic;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using System.Threading.Tasks;
using System.IO;

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

            protected override async Task<bool> CheckAsync()
            {
                // Can only delete files on the same VM where the FH instance that took the job is running.
                if (repairConfiguration.NodeName != RepairTaskManager.Context.NodeContext.NodeName)
                {
                    return false;
                }

                bool recurseSubDirectories = false;
                string path = Input.Arguments[0].Value.GetEffectiveTerm().GetStringValue();
                
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new GuanException("You must specify a full folder path as the first argument of DeleteFiles predicate.");
                }

                if (!Directory.Exists(path))
                {
                    throw new GuanException($"{path} does not exist.");
                }

                // default as 0 means delete all files.
                long maxFilesToDelete = 0;
                string searchPattern = null;
                FileSortOrder direction = FileSortOrder.Ascending;
                int count = Input.Arguments.Count;

                for (int i = 1; i < count; i++)
                {
                    switch (Input.Arguments[i].Name.ToLower())
                    {
                        case "sortorder":
                            direction = (FileSortOrder)Enum.Parse(typeof(FileSortOrder), Input.Arguments[i].Value.GetEffectiveTerm().GetStringValue());
                            break;

                        case "maxfilestodelete":
                            maxFilesToDelete = (long)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        case "recursesubdirectories":
                            _ = bool.TryParse(Input.Arguments[i].Value.GetStringValue(), out recurseSubDirectories);
                            break;

                        case "searchpattern":
                            searchPattern = Input.Arguments[i].Value.GetStringValue();
                            break;

                        default:
                            throw new GuanException($"Unsupported input: {Input.Arguments[i].Name}");
                    }
                }

                if (searchPattern != null)
                {
                    if (!ValidateFileSearchPattern(searchPattern, path))
                    {
                        await RepairTaskManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                        LogLevel.Info,
                                                        "DeleteFilesPredicateType::NoFilesMatchSearchPattern",
                                                        $"Specified search pattern, {searchPattern}, does not match any files in {path}.",
                                                        RepairTaskManager.Token).ConfigureAwait(false);
                        return false;
                    }
                }

                // RepairPolicy (base)
                repairConfiguration.RepairPolicy.RepairAction = RepairActionType.DeleteFiles;
                repairConfiguration.RepairPolicy.TargetType = RepairTargetType.VirtualMachine;
                repairConfiguration.RepairPolicy.RepairId = FOHealthData.RepairId;
                
                // DiskRepairPolicy (derives from RepairPolicy)
                (repairConfiguration.RepairPolicy as DiskRepairPolicy).FolderPath = path;
                (repairConfiguration.RepairPolicy as DiskRepairPolicy).MaxNumberOfFilesToDelete = maxFilesToDelete;
                (repairConfiguration.RepairPolicy as DiskRepairPolicy).FileAgeSortOrder = direction;
                (repairConfiguration.RepairPolicy as DiskRepairPolicy).RecurseSubdirectories = recurseSubDirectories;
                (repairConfiguration.RepairPolicy as DiskRepairPolicy).FileSearchPattern = !string.IsNullOrWhiteSpace(searchPattern) ? searchPattern : "*";

                // Try to schedule repair with RM.
                var repairTask = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                              () => RepairTaskManager.ScheduleFabricHealerRepairTaskAsync(
                                                                                        repairConfiguration,
                                                                                        RepairTaskManager.Token),
                                                               RepairTaskManager.Token).ConfigureAwait(false);

                if (repairTask == null)
                {
                    return false;
                }

                // Try to execute repair (FH executor does this work and manages repair state through RM, as always).
                bool success = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                            () => RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync(
                                                                                        repairTask,
                                                                                        repairConfiguration,
                                                                                        RepairTaskManager.Token),
                                                             RepairTaskManager.Token).ConfigureAwait(false);
                return success;
            }
        }

        public static DeleteFilesPredicateType Singleton(string name, RepairTaskManager repairTaskManager, TelemetryData foHealthData)
        {
            FOHealthData = foHealthData;
            RepairTaskManager = repairTaskManager;

            return Instance ??= new DeleteFilesPredicateType(name);
        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }

        private DeleteFilesPredicateType(string name)
                 : base(name, true, 1, 5)
        {

        }

        private static bool ValidateFileSearchPattern(string searchPattern, string path)
        {
            if (string.IsNullOrWhiteSpace(searchPattern) || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return false;
            }

            if (Directory.GetFiles(path, searchPattern).Length > 0)
            {
                return true;
            }

            return false;
        }
    }
}
