// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;

namespace FabricHealer.Repair
{
    public class RepairConfiguration
    {
        public Uri AppName
        {
            get; set;
        }

        public string ContainerId
        {
            get; set;
        }

        public string NodeType
        {
            get; set;
        }

        public string NodeName
        {
            get; set;
        }

        public Guid PartitionId 
        { 
            get; set; 
        } = Guid.Empty;

        public int ProcessId
        {
            get; set;
        }

        public RepairPolicy RepairPolicy
        {
            get; set;
        }

        public long ReplicaOrInstanceId 
        { 
            get; set; 
        }

        public Uri ServiceName
        {
            get; set;
        }

        public string FOErrorCode
        {
            get; set;
        }

        public object FOHealthMetricValue
        {
            get; set;
        }

        public string SystemServiceProcessName 
        { 
            get; set; 
        }
    }
}