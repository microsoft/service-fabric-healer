// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Runtime.Serialization;

namespace FabricHealerProxy
{
    /// <summary>
    /// Exception thrown when specified node does not exist in the cluster.
    /// </summary>
    [Serializable]
    public class FabricNodeNotFoundException : Exception
    {
        /// <summary>
        /// Creates an instance of FabricNodeNotFoundException.
        /// </summary>
        /// <param name="message"></param>
        public FabricNodeNotFoundException(string message)
            : base(message)
        {

        }

        /// <summary>
        /// Creates an instance of FabricNodeNotFoundException.
        /// </summary>
        /// <param name="message">Error message that describes the problem.</param>
        /// <param name="innerException">InnerException instance.</param>
        public FabricNodeNotFoundException(string message, Exception innerException) : base(message, innerException)
        {
        }

        /// <summary>
        /// Creates an instance of FabricNodeNotFoundException.
        /// </summary>
        /// <param name="info">SerializationInfo</param>
        /// <param name="context">StreamingContext</param>
        protected FabricNodeNotFoundException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
