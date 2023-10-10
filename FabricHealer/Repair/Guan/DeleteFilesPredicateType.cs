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
using System.Text.RegularExpressions;
using System.Threading;
using System.Fabric.Repair;

namespace FabricHealer.Repair.Guan
{
    public class DeleteFilesPredicateType : PredicateType
    {
        private static TelemetryData RepairData;
        private static DeleteFilesPredicateType Instance;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

            }

            protected override async Task<bool> CheckAsync()
            {
                // Can only delete files on the same VM where the FH instance that took the job is running.
                if (RepairData.NodeName != FabricHealerManager.ServiceContext.NodeContext.NodeName)
                {
                    return false;
                }

                if (FabricHealerManager.InstanceCount is (-1) or > 1)
                {
                    await FabricHealerManager.RandomWaitAsync();
                }

                if (FabricHealerManager.ConfigSettings.EnableLogicRuleTracing)
                {
                    _ = await RepairTaskEngine.TryTraceCurrentlyExecutingRuleAsync(Input.ToString(), RepairData, FabricHealerManager.Token);
                }

                RepairData.RepairPolicy.RepairAction = RepairActionType.DeleteFiles;
                bool recurseSubDirectories = false;
                string path = Input.Arguments[0].Value.GetEffectiveTerm().GetStringValue();
                
                // Contains env variable(s)?
                if (path.Contains('%'))
                {
                    if (Regex.Match(path, @"^%[a-zA-Z0-9_]+%").Success)
                    {
                        path = Environment.ExpandEnvironmentVariables(path);
                    }
                }

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
                TimeSpan maxHealthCheckOk = TimeSpan.Zero, maxExecutionTime = TimeSpan.Zero;

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
                            recurseSubDirectories = (bool)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        case "searchpattern":
                            searchPattern = Input.Arguments[i].Value.GetEffectiveTerm().GetStringValue();
                            break;

                        case "maxwaittimeforhealthstateok":
                            maxHealthCheckOk = (TimeSpan)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        case "maxexecutiontime":
                            maxExecutionTime = (TimeSpan)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        default:
                            await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                   LogLevel.Warning,
                                   "DeleteFilesPredicateType::InvalidArgument",
                                   "DeleteFilesPredicateType failure: Invalid argument specified - " + Input.Arguments[i].Name,
                                   FabricHealerManager.Token);

                            return false;
                    }
                }

                if (searchPattern != null)
                {
                    if (!ValidateFileSearchPattern(searchPattern, path, recurseSubDirectories))
                    {
                        await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                "DeleteFilesPredicateType::NoFilesMatchSearchPattern",
                                $"Specified search pattern, {searchPattern}, does not match any files in {path}.",
                                FabricHealerManager.Token);

                        return false;
                    }
                }

                string id = RepairData.RepairPolicy.RepairId;
                bool doHealthChecks = RepairData.RepairPolicy.DoHealthChecks;

                // DiskRepairPolicy
                DiskRepairPolicy diskRepairPolicy = new()
                {
                    Code = RepairData.Code,
                    FolderPath = path,
                    NodeName = RepairData.NodeName,
                    MaxNumberOfFilesToDelete = maxFilesToDelete,
                    FileAgeSortOrder = direction,
                    RecurseSubdirectories = recurseSubDirectories,
                    FileSearchPattern = !string.IsNullOrWhiteSpace(searchPattern) ? searchPattern : "*",
                    RepairId = id,
                    MaxTimePostRepairHealthCheck = maxHealthCheckOk,
                    DoHealthChecks = doHealthChecks,
                    RepairAction = RepairActionType.DeleteFiles
                };

                RepairData.RepairPolicy = diskRepairPolicy;
                RepairData.RepairPolicy.RepairIdPrefix = RepairConstants.FHTaskIdPrefix;
                RepairData.RepairPolicy.MaxExecutionTime = maxExecutionTime;
                RepairData.RepairPolicy.MaxTimePostRepairHealthCheck = maxHealthCheckOk;

                // Try to schedule repair with RM.
                RepairTask repairTask = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                              () => RepairTaskManager.ScheduleFabricHealerRepairTaskAsync(
                                                      RepairData,
                                                      FabricHealerManager.Token),
                                               FabricHealerManager.Token);
                if (repairTask == null)
                {
                    return false;
                }

                // MaxExecutionTime impl.
                using (CancellationTokenSource tokenSource = new())
                {
                    using (var linkedCTS = CancellationTokenSource.CreateLinkedTokenSource(tokenSource.Token, FabricHealerManager.Token))
                    {
                        tokenSource.CancelAfter(maxExecutionTime > TimeSpan.Zero ? maxExecutionTime : TimeSpan.FromMinutes(10));
                        tokenSource.Token.Register(() =>
                        {
                            _ = FabricHealerManager.TryCleanUpOrphanedFabricHealerRepairJobsAsync();
                        });

                        bool success = false;

                        // Try to execute repair (FH executor does this work and manages repair state through RM, as always).
                        try
                        {
                            success = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                               () => RepairTaskManager.ExecuteFabricHealerRepairTaskAsync(
                                                       repairTask,
                                                       RepairData,
                                                       linkedCTS.Token),
                                                   linkedCTS.Token);
                        }
                        catch (Exception e) when (e is not OutOfMemoryException)
                        {
                            if (e is not TaskCanceledException and not OperationCanceledException)
                            {
                                string message = $"Failed to execute {RepairData.RepairPolicy.RepairAction} for repair {RepairData.RepairPolicy.RepairId}: {e.Message}";
#if DEBUG
                                message += $"{Environment.NewLine}{e}";
#endif
                                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "DeleteFilesPredicateType::HandledException",
                                        message,
                                        FabricHealerManager.Token);
                            }

                            success = false;
                        }

                        // Best effort FH repair job cleanup retry.
                        if (!success && linkedCTS.IsCancellationRequested)
                        {
                            await FabricHealerManager.TryCleanUpOrphanedFabricHealerRepairJobsAsync();
                            return true;
                        }

                        return success;
                    }
                }
            }
        }

        public static DeleteFilesPredicateType Singleton(string name, TelemetryData repairData)
        {
            RepairData = repairData;
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

        private static bool ValidateFileSearchPattern(string searchPattern, string path, bool recurse)
        {
            if (string.IsNullOrWhiteSpace(searchPattern) || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return false;
            }

            try
            {
                if (Directory.GetFiles(path, searchPattern, recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Length > 0)
                {
                    return true;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {

            }

            return false;
        }
    }
}
