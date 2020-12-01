// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;

namespace FabricHealer.Repair
{
    /// <summary>
    /// Defines the type of repair to execute as specified in Settings.xml configuration Sections.
    /// </summary>
    public class RepairPolicy
    {
        public string Id 
        { 
            get; set; 
        }

        public CycleTimeDistributionType CycleTimeDistributionType 
        { 
            get; set; 
        }

        public RepairAction CurrentAction
        {
            get;set;
        }

        public long MaxRepairCycles
        { 
            get; set; 
        }

        public TimeSpan RepairCycleTimeWindow 
        { 
            get; set;
        } = TimeSpan.MinValue;

        public bool RepairInWarningState 
        { 
            get; set; 
        }

        public TimeSpan RunInterval 
        { 
            get; set; 
        } = TimeSpan.MinValue;

        public RepairTargetType TargetType
        {
            get; set;
        }
    }

    /// <summary>
    /// Type of interval time distribution to employ for a repair cycle's time window.
    /// </summary>
    public enum CycleTimeDistributionType
    {
        Even,
        // TODO?...
        /*Exponential,
        Random,
        Unknown,*/
    }
}
