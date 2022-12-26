// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Microsoft.Win32;

namespace FabricHealer.Utilities
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "This is only ever called when running on Windows..")]
    public class WindowsServiceFabricConfiguration : ServiceFabricConfiguration
    {
        private const string ServiceFabricWindowsRegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Service Fabric";

        public override string FabricVersion => GetString(nameof(FabricVersion));

        public override string FabricRoot => GetString(nameof(FabricRoot));

        public override string GetString(string name) => (string)Registry.GetValue(ServiceFabricWindowsRegistryPath, name, null);

        public override int GetInt32(string name) => (int)Registry.GetValue(ServiceFabricWindowsRegistryPath, name, 0);
    }
}
