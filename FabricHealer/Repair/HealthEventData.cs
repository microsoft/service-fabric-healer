// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric.Health;
using FabricHealer.Utilities.Telemetry;

namespace FabricHealer.Repair
{
    public sealed class HealthEventData
    {
        public string EntityName { get; set; }
        public EntityType EntityType { get; set; }
        public HealthState HealthState { get; set; }
        public DateTime LastErrorTransitionAt { get; set; }
        public DateTime SourceUtcTimestamp { get; set; }
        public string SourceId { get; set; }
        public string Property { get; set; }
    }
}
