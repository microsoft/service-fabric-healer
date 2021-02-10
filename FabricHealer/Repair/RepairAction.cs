// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricHealer.Repair
{
    /// <summary>
    /// Not all of these actions have corresponding implementations yet.
    /// </summary>
    public enum RepairAction
    {
        DeleteFiles,
        PauseFabricNode,
        RemoveFabricNodeState,
        RemoveReplica,
        RepairPartition,
        RestartCodePackage,
        RestartFabricNode,
        RestartReplica,
        RestartVM,
    }
}