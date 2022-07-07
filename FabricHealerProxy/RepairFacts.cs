// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Newtonsoft.Json;
using System;
using System.Fabric.Health;
using System.Runtime.InteropServices;

namespace FabricHealer
{
    /// <summary>
    /// Data type that houses facts that FabricHealer expects and therefore understands. This type is serialized by ServiceFabricHealthReporter and used as the Description property 
    /// of the related Service Fabric Health Event. FabricHealer will deserialize the serialized instance and use the facts it contains throughout its mitigation infrastructure.
    /// Effectively, this type enables structured (well-known data) inter-service communication via Service Fabric health reports.
    /// </summary>
    public class RepairFacts : ITelemetryData
    {
        private readonly string _os;

        /// <inheritdoc/>
        public string ApplicationName
        {
            get; set;
        }
        /// <inheritdoc/> 
        public string Code
        {
            get; set;
        }
        /// <inheritdoc/>
        public string ContainerId
        {
            get; set;
        }
        /// <inheritdoc/>
        public string Description
        {
            get; set;
        }
        /// <inheritdoc/>
        public EntityType EntityType
        {
            get; set;
        }
        /// <inheritdoc/>
        public HealthState HealthState
        {
            get; set;
        } = HealthState.Warning;
        /// <inheritdoc/>
        public string Metric
        {
            get; set;
        }
        /// <inheritdoc/>
        public string NodeName
        {
            get; set;
        }
        /// <inheritdoc/>
        public string NodeType
        {
            get; set;
        }
        /// <inheritdoc/>
        public string OS
        {
            get { return _os; }
        }
        /// <inheritdoc/>
        public Guid? PartitionId
        {
            get; set;
        }
        /// <inheritdoc/>
        public long ProcessId
        {
            get; set;
        }
        /// <inheritdoc/>
        public long ReplicaId
        {
            get; set;
        }
        /// <inheritdoc/>
        public string ServiceName
        {
            get; set;
        }
        /// <inheritdoc/>
        public string Source
        {
            get; set;
        }
        /// <inheritdoc/>
        public string ProcessName
        {
            get;set;
        }
        /// <inheritdoc/>
        public string Property
        {
            get; set;
        }
        /// <inheritdoc/>
        public double Value
        {
            get; set;
        }
        /// <inheritdoc/>
        [JsonConstructor]
        public RepairFacts()
        {
            _os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux";
        }
    }
}
