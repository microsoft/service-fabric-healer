// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------
using FabricHealer.TelemetryLib;

namespace FabricHealer.Utilities.Telemetry
{
    internal class ClusterTelemetryData
    {
        public string ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId;

        public string Description { get; set; }

        public LogLevel LogLevel { get; set; }
    }
}