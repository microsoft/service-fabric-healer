// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Newtonsoft.Json;
using System.Fabric.Health;
using System;
using FabricHealer.Repair;
using System.Diagnostics.Tracing;

namespace FabricHealer.Utilities.Telemetry
{
    [EventData]
    [Serializable]
    public class TelemetryData
    {
        private readonly string _os;

        public string ApplicationName
        {
            get; set;
        }

        public string Code
        {
            get; set;
        }

        public string ContainerId
        {
            get; set;
        }

        public string ClusterId
        {
            get; set;
        }

        public string Description
        {
            get; set;
        }

        [EventField]
        public EntityType EntityType
        {
            get; set;
        }

        [EventField]
        public HealthState HealthState
        {
            get; set;
        }
 
        public string Metric
        {
            get; set;
        }

        public string NodeName
        {
            get; set;
        }

        /// <summary>
        /// The name of the FabricObserver observer that generated the health information.
        /// </summary>
        public string ObserverName
        {
            get; set;
        }

        public string OS
        {
            get { return _os; }
        }
       
        [EventField]
        public Guid? PartitionId
        {
            get; set;
        }

        public long ProcessId
        {
            get; set;
        }

        public long ReplicaId
        {
            get; set;
        }

        public string ReplicaRole
        {
            get; set;
        }

        public string ServiceKind
        {
            get; set;
        }

        public string ServiceName
        {
            get; set;
        }

        public string Source
        {
            get; set;
        }

        public string ProcessName
        {
            get; set;
        }

        public string ProcessStartTime
        {
            get; set;
        }

        public double Value
        {
            get; set;
        }

        public string NodeType
        {
            get; set;
        }

        public string Property
        {
            get; set;
        }

        [EventField]
        public RepairPolicy RepairPolicy
        {
            get; set;
        }

        [JsonConstructor]
        public TelemetryData()
        {
            _os = OperatingSystem.IsWindows() ? "Windows" : "Linux";
        }
    }
}