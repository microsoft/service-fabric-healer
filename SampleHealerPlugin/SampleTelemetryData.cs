// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Utilities.Telemetry;
using Newtonsoft.Json;
using System.Diagnostics.Tracing;

namespace FabricHealer.SamplePlugins
{
    [EventData]
    public class SampleTelemetryData : TelemetryData
    {
        private string customProperty;

        public string CustomProperty { get => customProperty; set => customProperty = value; }

        [JsonConstructor]
        public SampleTelemetryData()
        { 
        
        }
    }
}