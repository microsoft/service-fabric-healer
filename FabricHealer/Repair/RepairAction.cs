﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricHealer.Repair
{
    public enum RepairAction
    {
        DeleteFiles,
        Heal,
        PauseFabricNode,
        ReimageVM,
        RemoveFabricNodeState,
        RemoveReplica,
        RestartCodePackage,
        RestartFabricNode,
        RestartReplica,
        RestartVM,
    }
}