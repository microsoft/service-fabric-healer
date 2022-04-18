// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Runtime.Serialization;

namespace FabricHealerLib.Exceptions
{
    /// <summary>
    /// Exception thrown when RepairData instance is missing values for required non-null members (E.g., NodeName).
    /// </summary>
    [Serializable]
    public class MissingRepairDataException : Exception
    {
        /// <summary>
        /// Creates an instance of MissingRequiredDataException.
        /// </summary>
        public MissingRepairDataException()
        {
        }

        /// <summary>
        ///  Creates an instance of MissingRequiredDataException.
        /// </summary>
        /// <param name="message">Error message that describes the problem.</param>
        public MissingRepairDataException(string message) : base(message)
        {
        }

        /// <summary>
        /// Creates an instance of MissingRequiredDataException.
        /// </summary>
        /// <param name="message">Error message that describes the problem.</param>
        /// <param name="innerException">InnerException instance.</param>
        public MissingRepairDataException(string message, Exception innerException) : base(message, innerException)
        {
        }

        /// <summary>
        /// Creates an instance of MissingRequiredDataException.
        /// </summary>
        /// <param name="info">SerializationInfo</param>
        /// <param name="context">StreamingContext</param>
        protected MissingRepairDataException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}