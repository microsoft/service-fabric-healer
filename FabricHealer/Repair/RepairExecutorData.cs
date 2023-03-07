// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Runtime.Serialization;

namespace FabricHealer.Repair
{
    /// <summary>
    /// RepairExecutorData is used to store custom FH state for an executing repair task.
    /// </summary>
    [DataContract]
    public class RepairExecutorData
    {
        /// <summary>
        /// Holds relevant repair state and policy information.
        /// </summary>
        [DataMember]
        public RepairPolicy RepairPolicy
        {
            get; set;
        }
    }
}
