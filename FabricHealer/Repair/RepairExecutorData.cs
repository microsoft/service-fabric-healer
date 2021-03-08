// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Runtime.Serialization;

namespace FabricHealer.Repair
{
    /// <summary>
    /// Executor Data is used to store the state of an executing repair task.
    /// </summary>
    [DataContract]
    public class RepairExecutorData
    {
        [DataMember]
        public int ExecutorSubState 
        { 
            get; set; 
        }

        [DataMember] 
        public int ExecutorTimeoutInMinutes
        {
            get; set;
        }

        [DataMember] 
        public DateTime RestartRequestedTime
        {
            get; set;
        }

        [DataMember]
        public string CustomIdentificationData
        {
            get; set;
        }

        [DataMember]
        public FabricNodeRepairStep LatestRepairStep
        {
            get; set;
        } = FabricNodeRepairStep.Scheduled;

        [DataMember]
        public string NodeType
        {
            get; set;
        }

        [DataMember]
        public string NodeName
        {
            get; set;
        }

        [DataMember]
        public RepairPolicy RepairPolicy
        {
            get; set;
        }

        [DataMember]
        public string FOErrorCode
        {
            get; set;
        }

        [DataMember]
        public object FOMetricValue
        {
            get; set;
        }

        [DataMember]
        public Guid PartitionId 
        { 
            get; internal set; 
        }

        [DataMember]
        public long ReplicaOrInstanceId 
        { 
            get; internal set; 
        }

        [DataMember]
        public Uri ServiceName 
        { 
            get; internal set; 
        }

        [DataMember]
        public string SystemServiceProcessName 
        {
            get; internal set; 
        }
    }
}
