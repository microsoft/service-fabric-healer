// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Utilities.Telemetry;
using System;
using System.Runtime.Serialization;

namespace FabricHealer.Repair
{
    /// <summary>
    /// RepairExecutorData is used to store custom FH state for an executing repair task.
    /// </summary>
    [DataContract]
    public class RepairExecutorData
    {
        [DataMember] 
        public int ExecutorTimeoutInMinutes
        {
            get; set;
        }

        [DataMember]
        public FabricNodeRepairStep LatestRepairStep
        {
            get; set;
        } = FabricNodeRepairStep.Scheduled;

        [DataMember]
        public TelemetryData RepairData
        {
            get; set;
        }
    }
}
