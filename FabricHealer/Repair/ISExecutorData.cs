// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Runtime.Serialization;

namespace FabricHealer.Repair
{
    /// <summary>
    /// ISExecutorData is used to store custom FH state for an executing repair task.
    /// </summary>
    [DataContract]
    public class ISExecutorData
    {
        [DataMember]
        public string JobId
        {
            get; set;
        }

        [DataMember]
        public string UD
        {
            get; set;
        }

        [DataMember]
        public string StepId
        {
            get; set;
        }
    }
}
