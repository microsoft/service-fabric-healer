// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;

namespace FabricHealer.Repair
{
    /// <summary>
    /// The type of repair action.
    /// Not all of these have implementations yet.
    /// </summary>
    [Serializable]
    public enum RepairActionType
    {
        DeleteFiles,
        RemoveFabricNodeState,
        RemoveReplica,
        RepairPartition,
        RestartCodePackage,
        RestartFabricNode,
        RestartProcess,
        RestartReplica,
        RebootMachine,
        ReimageDisk,
        ReimageOS
    }
}