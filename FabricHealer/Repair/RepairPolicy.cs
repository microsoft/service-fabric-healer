// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricHealer.Repair
{
    /// <summary>
    /// Defines the type of repair to execute.
    /// </summary>
    public class RepairPolicy
    {
        public string RepairId 
        { 
            get; set; 
        }

        public RepairActionType RepairAction
        {
            get; set;
        }

        public RepairTargetType TargetType
        {
            get; set;
        }
    }
}
