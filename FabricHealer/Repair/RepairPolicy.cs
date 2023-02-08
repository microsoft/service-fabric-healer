// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics.Tracing;
using System.Fabric.Health;

namespace FabricHealer.Repair
{
    /// <summary>
    /// Defines the type of repair to execute.
    /// </summary>
    [EventData]
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
        /// Maximum amount of time a repair can execute. This is related to repairs where FabricHealer is the executor.
        /// </summary>
        public TimeSpan MaxExecutionTime
        { 
            get; set; 
        } = TimeSpan.Zero;

        /// <summary>
        /// The type of repair execution (RestartCodePackage, RestartReplica, etc..)
        /// </summary>
        [EventField]
        public RepairActionType RepairAction
        {
            get; set;
        }

        /// <summary>
        /// The name of the infrastucture repair to provide to RM that IS will execute.
        /// </summary>
        public string InfrastructureRepairName
        {
            get; set;
        }

        /// <summary>
        /// Maximum amount of time to check if health state of repaired target entity is Ok.
        /// </summary>
        public TimeSpan MaxTimePostRepairHealthCheck
        {
            get; set;
        } = TimeSpan.Zero;

        /// <summary>
        /// Whether or not RepairManager should do preparing and restoring health checks before approving the target repair job.
        /// Setting this to true will increase the time it takes to complete a repair.
        /// </summary>
        public bool DoHealthChecks
        {
            get; set;
        }

        /// <summary>
        /// The maximum number of currently executing machine repairs in the cluster allowed.
        /// </summary>
        public long MaxConcurrentRepairs
        {
            get; set;
        }

        /// <summary>
        /// The repair ID prefix used to associate an FH repair to its executor (FH or FH_Infra, for example).
        /// </summary>
        public string RepairIdPrefix
        {
            get; set;
        }

        /// <summary>
        /// The target node name;
        /// </summary>
        public string NodeName
        {
            get; set;
        }

        /// <summary>
        /// The target service name;
        /// </summary>
        public string ServiceName
        {
            get; set;
        }

        /// <summary>
        /// The target app name;
        /// </summary>
        public string AppName
        {
            get; set;
        }

        public string Code
        {
            get; set;
        }

        public string ProcessName
        {
            get; set;
        }

        [EventField]
        public HealthState HealthState
        {
            get; set;
        }
    }
}