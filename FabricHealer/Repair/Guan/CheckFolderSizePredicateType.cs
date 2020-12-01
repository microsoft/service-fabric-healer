// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
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
                string folderPath = (string)Input.Arguments[0].Value.GetEffectiveTerm().GetValue();
                long maxFolderSizeGB = (long)Input.Arguments[1].Value.GetEffectiveTerm().GetValue();
                long size = GetFolderSizeGB(folderPath);

                if (size >= maxFolderSizeGB)
                {
                    return true;
                }

                string message =
                $"Repair {FOHealthData.RepairId}: Supplied MaxFolderSizeGB value ({maxFolderSizeGB}) for path {folderPath} is less than computed folder size ({size}). " +
                $"Will not attempt repair.";

                RepairTaskHelper.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "CheckFolderSizePredicate",
                    message,
                    RepairTaskHelper.Token).GetAwaiter().GetResult();

                return false;
            }
            
            private long GetFolderSizeGB(string path)
            {
                var dir = new DirectoryInfo(path);
               
                if (!dir.Exists)
                {
                    RepairTaskHelper.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "CheckFolderSizePredicate::DirectoryNotFound",
                        $"Directory {path} does not exist.",
                        RepairTaskHelper.Token).GetAwaiter().GetResult();
                            
                    return 0;
                }

                if (dir.GetFiles().Length == 0)
                {
                        RepairTaskHelper.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "CheckFolderSizePredicate::NoFilesFound",
                            $"Directory {path} does not contain any files.",
                            RepairTaskHelper.Token).GetAwaiter().GetResult();

                        return 0;
                }
               
                var folderSizeInBytes = dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
                
                RepairTaskHelper.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "CheckFolderSizePredicate::Size",
                            $"Directory {path} size: {folderSizeInBytes} bytes.",
                            RepairTaskHelper.Token).GetAwaiter().GetResult();

                return folderSizeInBytes / 1024 / 1024 / 1024;
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
}
