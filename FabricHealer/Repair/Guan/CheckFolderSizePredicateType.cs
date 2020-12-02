﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using Guan.Common;
using Guan.Logic;
using System.IO;
using System.Linq;

namespace FabricHealer.Repair.Guan
{
    public class CheckFolderSizePredicateType : PredicateType
    {
        private static CheckFolderSizePredicateType Instance;
        private static RepairTaskHelper RepairTaskHelper;
        private static TelemetryData FOHealthData;

        class Resolver : BooleanPredicateResolver
        {
            public Resolver(
                CompoundTerm input,
                Constraint constraint,
                QueryContext context)
                : base(input, constraint,  context)
            {

            }

            protected override bool Check()
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
                            folderPath = (string)Input.Arguments[i].Value.GetEffectiveTerm().GetValue();
                            break;

                        case "maxfoldersizemb":
                            maxFolderSizeMB = (long)Input.Arguments[i].Value.GetEffectiveTerm().GetValue();
                            break;

                        case "maxfoldersizegb":
                            maxFolderSizeGB = (long)Input.Arguments[i].Value.GetEffectiveTerm().GetValue();
                            break;

                        default:
                            throw new GuanException($"Unsupported input: {Input.Arguments[i].Name}");
                    }
                }

                if (!Directory.Exists(folderPath))
                {
                    RepairTaskHelper.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "CheckFolderSizePredicate::DirectoryNotFound",
                        $"Directory {folderPath} does not exist.",
                        RepairTaskHelper.Token).GetAwaiter().GetResult();

                    return false;
                }

                if (Directory.GetFiles(folderPath, "*", new EnumerationOptions { RecurseSubdirectories = true }).Length == 0)
                {
                    RepairTaskHelper.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "CheckFolderSizePredicate::NoFilesFound",
                        $"Directory {folderPath} does not contain any files.",
                        RepairTaskHelper.Token).GetAwaiter().GetResult();

                    return false;
                }

                long size = 0;

                if (maxFolderSizeGB > 0)
                {
                    size = GetFolderSize(folderPath, SizeUnit.GB);

                    if (size >= maxFolderSizeGB)
                    {
                        return true;
                    }
                }
                else if (maxFolderSizeMB > 0)
                {
                    size = GetFolderSize(folderPath, SizeUnit.MB);

                    if (size >= maxFolderSizeMB)
                    {
                        return true;
                    }
                }

                string message =
                $"Repair {FOHealthData.RepairId}: Supplied Maximum folder size value ({(maxFolderSizeGB > 0 ? maxFolderSizeGB.ToString() + "GB" : maxFolderSizeMB.ToString() + "MB")}) " +
                $"for path {folderPath} is less than computed folder size ({size}{(maxFolderSizeGB > 0 ? "GB" : "MB")}). " +
                $"Will not attempt repair.";

                RepairTaskHelper.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "CheckFolderSizePredicate",
                    message,
                    RepairTaskHelper.Token).GetAwaiter().GetResult();

                return false;
            }
            
            private long GetFolderSize(string path, SizeUnit unit)
            {
                var dir = new DirectoryInfo(path);
                var folderSizeInBytes = dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
                
                RepairTaskHelper.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "CheckFolderSizePredicate::Size",
                            $"Directory {path} size: {folderSizeInBytes} bytes.",
                            RepairTaskHelper.Token).GetAwaiter().GetResult();

                if (unit == SizeUnit.GB)
                {
                    return folderSizeInBytes / 1024 / 1024 / 1024;
                }

                return folderSizeInBytes / 1024 / 1024;
            }
        }

        public static CheckFolderSizePredicateType Singleton(
            string name,
            RepairTaskHelper repairTaskHelper,
            TelemetryData foHealthData)
        {
            FOHealthData = foHealthData;
            RepairTaskHelper = repairTaskHelper;
            return Instance ??= new CheckFolderSizePredicateType(name);
        }

        private CheckFolderSizePredicateType(
            string name)
            : base(name, true, 2, 2)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }

    enum SizeUnit
    {
        GB,
        MB
    }
}
