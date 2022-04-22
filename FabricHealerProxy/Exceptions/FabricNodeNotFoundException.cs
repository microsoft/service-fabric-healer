﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Runtime.Serialization;

namespace FabricHealerProxy
{
    /// <summary>
    /// Exception thrown when a specified node does not exist in the cluster.
    /// </summary>
    [Serializable]
    public class NodeNotFoundException : Exception
    {
        /// <summary>
        /// Creates an instance of NodeNotFoundException.
        /// </summary>
        public NodeNotFoundException()
        {
        }

        /// <summary>
        /// Creates an instance of FabricNodeNotFoundException.
        /// </summary>
        /// <param name="message">Error message that describes the problem.</param>
        public NodeNotFoundException(string message) : base(message)
        {
        }

        /// <summary>
        /// Creates an instance of FabricNodeNotFoundException.
        /// </summary>
        /// <param name="message">Error message that describes the problem.</param>
        /// <param name="innerException">InnerException instance.</param>
        public NodeNotFoundException(string message, Exception innerException) : base(message, innerException)
        {
        }

        /// <summary>
        /// Creates an instance of FabricNodeNotFoundException.
        /// </summary>
        /// <param name="info">SerializationInfo</param>
        /// <param name="context">StreamingContext</param>
        protected NodeNotFoundException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
