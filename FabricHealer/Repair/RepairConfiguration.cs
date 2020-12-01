// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric.Query;

namespace FabricHealer.Repair
{
    public class RepairConfiguration
    {
        public Uri AppName
        {
            get; set;
        }

        public DeployedCodePackage CodePackage
        {
            get; set;
        }

        public string ContainerId
        {
            get; set;
        }

        public string FolderPath
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

        public RepairPolicy RepairPolicy
        {
            get; set;
        }

        public long ReplicaOrInstanceId 
        { 
            get; set; 
        } = default;

        public Uri ServiceName
        {
            get; set;
        }

        public string FOHealthCode
        {
            get; set;
        }

        public object FOHealthMetricValue
        {
            get; set;
        }
    }
}