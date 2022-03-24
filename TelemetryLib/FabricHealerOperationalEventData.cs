// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Runtime.InteropServices;

namespace FabricHealer.TelemetryLib
{
    public class FabricHealerOperationalEventData
    {
        public string UpTime
        {
            get; set;
        }

        public string Version
        {
            get; set;
        }

        public string SFRuntimeVersion
        {
            get; set;
        }

        public RepairData RepairData
        {
            get; set;
        }

        public string OS => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux";
    }
}
