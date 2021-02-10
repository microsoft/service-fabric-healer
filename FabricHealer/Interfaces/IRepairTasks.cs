// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;
using System.Fabric.Repair;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Repair;
using FabricHealer.Utilities.Telemetry;

namespace FabricHealer.Interfaces
{
    public interface IRepairTasks
    {
        Task ActivateServiceFabricNodeAsync(string nodeName, CancellationToken cancellationToken);

        Task RemoveServiceFabricNodeStateAsync(string nodeName, CancellationToken cancellationToken);

        Task<bool> RestartDeployedCodePackageAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken);
        
        Task<bool> RestartReplicaAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken);

        Task<bool> RemoveReplicaAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken);

        Task<bool> SafeRestartServiceFabricNodeAsync(string nodeName, RepairTask repairTask, CancellationToken cancellationToken);

        Task StartRepairWorkflowAsync(TelemetryData foHealthData, List<string> repairRules, CancellationToken cancellationToken);
    }
}