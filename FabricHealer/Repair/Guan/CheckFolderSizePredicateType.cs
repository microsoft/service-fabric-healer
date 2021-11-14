// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.IO;
using System.Linq;
using Guan.Logic;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using System.Threading.Tasks;

namespace FabricHealer.Repair.Guan
{
    public class CheckFolderSizePredicateType : PredicateType
    {
        private static CheckFolderSizePredicateType Instance;
        private static RepairTaskManager RepairTaskManager;
        private static TelemetryData FOHealthData;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint,  context)
            {

            }

            protected override async Task<bool> CheckAsync()
            {
                string folderPath = null;
                long maxFolderSizeGB = 0;
                long maxFolderSizeMB = 0;
                int count = Input.Arguments.Count;

                for (int i = 0; i < count; i++)
                {
                    switch (Input.Arguments[i].Name.ToLower())
                    {
                        case "folderpath":
                            folderPath = (string)Input.Arguments[i].Value.GetEffectiveTerm().GetStringValue();
                            break;

                        case "maxfoldersizemb":
                            maxFolderSizeMB = (long)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        case "maxfoldersizegb":
                            maxFolderSizeGB = (long)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        default:
                            throw new GuanException($"Unsupported input: {Input.Arguments[i].Name}");
                    }
                }

                if (!Directory.Exists(folderPath))
                {
#if DEBUG
                    await RepairTaskManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                            LogLevel.Info,
                                                            "CheckFolderSizePredicate::DirectoryNotFound",
                                                            $"Directory {folderPath} does not exist.",
                                                            RepairTaskManager.Token).ConfigureAwait(false);
#endif
                    return false;
                }

                if (Directory.GetFiles(folderPath, "*", new EnumerationOptions { RecurseSubdirectories = true }).Length == 0)
                {
#if DEBUG
                    await RepairTaskManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                            LogLevel.Info,
                                                            "CheckFolderSizePredicate::NoFilesFound",
                                                            $"Directory {folderPath} does not contain any files.",
                                                            RepairTaskManager.Token).ConfigureAwait(false);
#endif
                    return false;
                }

                long size = 0;

                if (maxFolderSizeGB > 0)
                {
                    size = await GetFolderSize(folderPath, SizeUnit.GB);

                    if (size >= maxFolderSizeGB)
                    {
                        return true;
                    }
                }
                else if (maxFolderSizeMB > 0)
                {
                    size = await GetFolderSize(folderPath, SizeUnit.MB);

                    if (size >= maxFolderSizeMB)
                    {
                        return true;
                    }
                }
#if DEBUG
                string message =
                $"Repair {FOHealthData.RepairId}: Supplied Maximum folder size value ({(maxFolderSizeGB > 0 ? maxFolderSizeGB + "GB" : maxFolderSizeMB + "MB")}) " +
                $"for path {folderPath} is less than computed folder size ({size}{(maxFolderSizeGB > 0 ? "GB" : "MB")}). " +
                "Will not attempt repair.";

                RepairTaskManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                        LogLevel.Info,
                                                        "CheckFolderSizePredicate",
                                                        message,
                                                        RepairTaskManager.Token).GetAwaiter().GetResult();
#endif
                return false;
            }
            
            private static async Task<long> GetFolderSize(string path, SizeUnit unit)
            {
                var dir = new DirectoryInfo(path);
                var folderSizeInBytes = dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
#if DEBUG
                await RepairTaskManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                        LogLevel.Info,
                                                        "CheckFolderSizePredicate::Size",
                                                        $"Directory {path} size: {folderSizeInBytes} bytes.",
                                                        RepairTaskManager.Token).ConfigureAwait(false);
#endif
                if (unit == SizeUnit.GB)
                {
                    return folderSizeInBytes / 1024 / 1024 / 1024;
                }

                return folderSizeInBytes / 1024 / 1024;
            }
        }

        public static CheckFolderSizePredicateType Singleton(string name, RepairTaskManager repairTaskManager, TelemetryData foHealthData)
        {
            FOHealthData = foHealthData;
            RepairTaskManager = repairTaskManager;

            return Instance ??= new CheckFolderSizePredicateType(name);
        }

        private CheckFolderSizePredicateType(string name)
                : base(name, true, 2, 2)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }

    internal enum SizeUnit
    {
        GB,
        MB
    }
}
