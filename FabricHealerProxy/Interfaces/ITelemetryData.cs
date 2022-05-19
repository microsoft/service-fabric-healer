﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric.Health;

namespace FabricHealerProxy.Interfaces
{
    /// <summary>
    /// 
    /// </summary>
    public interface ITelemetryData
    {
        /// <summary>
        /// Required if the repair target is a Service. Service Fabric ApplicationName as a string value (OriginalString on a Uri instance, for example).
        /// </summary>
        string ApplicationName { get; set; }
        /// <summary>
        /// Required. The supported error code.
        /// </summary>
        string Code { get; set; }
        /// <summary>
        /// Required if the repair target is a container. The id of the container.
        /// </summary>
        string ContainerId { get; set; }
        /// <summary>
        /// Optional. The description of the problem being reported.
        /// </summary>
        string Description { get; set; }
        /// <summary>
        /// The Service Fabric entity type to be repaired.
        /// </summary>
        EntityType EntityType { get; set; }
        /// <summary>
        /// This string value will be set by the consuming function, which requires a System.Fabric.HealthState enum parameter.
        /// </summary>
        HealthState HealthState { get; set; }
        /// <summary>
        /// Required. The supported resource usage metric name.
        /// </summary>
        string Metric { get; set; }
        /// <summary>
        /// Required. The name of the node where the entity resides.
        /// </summary>
        string NodeName { get; set; }
        /// <summary>
        /// The OS hosting Service Fabric. This is read-only.
        /// </summary>
        string OS { get; }
        /// <summary>
        /// Required if the repair target is a Service. The Partition Id (as a string) where the replica or instance resides that is in Error or Warning state.
        /// </summary>
        Guid? PartitionId { get; set; }
        /// <summary>
        /// Optional. The host process id of the Service entity.
        /// </summary>
        long ProcessId { get; set; }
        /// <summary>
        /// Required if the repair target is a Service. The Replica or Instance id of the target Service replica.
        /// </summary>
        long ReplicaId { get; set; }
        /// <summary>
        /// Required if the repair target is a Service. The name of the service (as a string). This would be the same value as the OriginalString property of the ServiceName Uri instance.
        /// </summary>
        string ServiceName { get; set; }
        /// <summary>
        /// Required. This is the name of the service (as a string) that is generating the health report with this TelemetryData instance.
        /// </summary>
        string Source { get; set; }
        /// <summary>
        /// Optional. This is required if you are targeting Service Fabric System Service process. In this case, you should also supply the related value for ProcessId.
        /// </summary>
        string SystemServiceProcessName { get; set; }
        /// <summary>
        /// Optional. The supported resource usage metric value. NOTE: This value must be supplied if you expect to use this fact in related FabricHealer logic rules.
        /// </summary>
        double Value { get; set; }
        /// <summary>
        /// Don't set. The Fabric node type. FabricHealer will set this.
        /// </summary>
        string NodeType { get; set; }
        /// <summary>
        /// Required. This will be used as the Health Event Property.
        /// </summary>
        string Property { get; set; }
    }
}
