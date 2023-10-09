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
using System;
using System.Text.RegularExpressions;

namespace FabricHealer.Repair.Guan
{
    public class CheckFolderSizePredicateType : PredicateType
    {
        private static CheckFolderSizePredicateType Instance;
        private static TelemetryData RepairData;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint,  context)
            {

            }

            protected override async Task<bool> CheckAsync()
            {
                int count = Input.Arguments.Count;

                if (count == 0)
                {
                    throw new GuanException("CheckFolderSizePredicateType: Must supply at least one argument (full path to folder).");
                }

                string folderPath = Input.Arguments[0].Value.GetEffectiveTerm().GetStringValue();

                long maxFolderSizeGB = 0;
                long maxFolderSizeMB = 0;
                
                for (int i = 1; i < count; i++)
                {
                    switch (Input.Arguments[i].Name.ToLower())
                    {
                        case "maxfoldersizemb":
                            maxFolderSizeMB = (long)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        case "maxfoldersizegb":
                            maxFolderSizeGB = (long)Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue();
                            break;

                        default:
                            throw new GuanException($"Unrecognized argument supplied: {Input.Arguments[i].Name}");
                    }
                }

                // Contains env variable(s)?
                if (folderPath.Contains('%'))
                {
                    if (Regex.Match(folderPath, @"^%[a-zA-Z0-9_]+%").Success)
                    {
                        folderPath = Environment.ExpandEnvironmentVariables(folderPath);
                    }
                }

                if (!Directory.Exists(folderPath))
                {
                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "CheckFolderSizePredicate::DirectoryNotFound",
                            $"Directory {folderPath} does not exist.",
                            FabricHealerManager.Token);

                    return false;
                }

                if (Directory.GetFiles(folderPath, "*", new EnumerationOptions { RecurseSubdirectories = true }).Length == 0)
                {
                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "CheckFolderSizePredicate::NoFilesFound",
                            $"Directory {folderPath} does not contain any files.",
                            FabricHealerManager.Token);

                        return false;
                }

                double size = 0.0;

                if (maxFolderSizeGB > 0)
                {
                    size = await GetFolderSizeAsync(folderPath, SizeUnit.GB);

                    if (size >= maxFolderSizeGB)
                    {
                        return true;
                    }
                }
                else if (maxFolderSizeMB > 0)
                {
                    size = await GetFolderSizeAsync(folderPath, SizeUnit.MB);

                    if (size >= maxFolderSizeMB)
                    {
                        return true;
                    }
                }

                string message =
                        $"Repair {RepairData.RepairPolicy.RepairId}: Supplied Maximum folder size value ({(maxFolderSizeGB > 0 ? maxFolderSizeGB + "GB" : maxFolderSizeMB + "MB")}) " +
                        $"for path {folderPath} is less than computed folder size ({size}{(maxFolderSizeGB > 0 ? "GB" : "MB")}). " +
                        "Will not attempt repair.";

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "CheckFolderSizePredicate",
                        message,
                        FabricHealerManager.Token);

                return false;
            }
            
            private static async Task<double> GetFolderSizeAsync(string path, SizeUnit unit)
            {
                var dir = new DirectoryInfo(path);
                double folderSizeInBytes = Convert.ToDouble(dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length));

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "CheckFolderSizePredicate::Size",
                        $"Directory {path} size: {folderSizeInBytes} bytes.",
                        FabricHealerManager.Token);

                if (unit == SizeUnit.GB)
                {
                    return folderSizeInBytes / 1024 / 1024 / 1024;
                }

                return folderSizeInBytes / 1024 / 1024;
            }
        }

        public static CheckFolderSizePredicateType Singleton(string name, TelemetryData repairData)
        {
            RepairData = repairData;
            return Instance ??= new CheckFolderSizePredicateType(name);
        }

        private CheckFolderSizePredicateType(string name)
                : base(name, true, 1)
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
