// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics.Tracing;

namespace FabricHealer.Repair
{
    /// <summary>
    /// Defines the type of repair to execute.
    /// </summary>
    [EventData]
    [Serializable]
    public class RepairPolicy
    {
        /// <summary>
        /// The unique ID of a FabricHealer Repair.
        /// </summary>
        public string RepairId 
        { 
            get; set; 
        }

        /// <summary>
        /// The type of repair execution (RestartCodePackage, RestartReplica, etc..)
        /// </summary>
        [EventField]
        public RepairActionType RepairAction
        {
            get; set;
        }

        /// <summary>
        /// Maximum amount of time to check if health state of repaired target entity is Ok.
        /// </summary>
        [EventField]
        public TimeSpan MaxTimePostRepairHealthCheck
        {
            get; set;
        } = TimeSpan.MinValue;

        /// <summary>
        /// Whether or not RepairManager should do preparing and restoring health checks before approving the target repair job.
        /// Setting this to true will increase the time it takes to complete a repair.
        /// </summary>
        [EventField]
        public bool DoHealthChecks
        {
            get; set;
        }
    }
}
