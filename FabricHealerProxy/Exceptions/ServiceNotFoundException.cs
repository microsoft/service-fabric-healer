// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Runtime.Serialization;

namespace FabricHealerProxy
{
    /// <summary>
    /// Exception thrown when a specified service does not exist in the cluster.
    /// </summary>
    [Serializable]
    public class ServiceNotFoundException : Exception
    {
        /// <summary>
        /// Creates an instance of ServiceNotFoundException.
        /// </summary>
        public ServiceNotFoundException()
        {
        }

        /// <summary>
        /// Creates an instance of ServiceNotFoundException.
        /// </summary>
        /// <param name="message">Error message that describes the problem.</param>
        public ServiceNotFoundException(string message) : base(message)
        {
        }

        /// <summary>
        /// Creates an instance of ServiceNotFoundException.
        /// </summary>
        /// <param name="message">Error message that describes the problem.</param>
        /// <param name="innerException">InnerException instance.</param>
        public ServiceNotFoundException(string message, Exception innerException) : base(message, innerException)
        {
        }

        /// <summary>
        /// Creates an instance of ServiceNotFoundException.
        /// </summary>
        /// <param name="info">SerializationInfo</param>
        /// <param name="context">StreamingContext</param>
        protected ServiceNotFoundException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}