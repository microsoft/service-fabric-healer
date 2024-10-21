// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Runtime.Serialization;

namespace FabricHealer
{
    /// <summary>
    /// Exception thrown when RepairFacts instance is missing values for required non-null members (E.g., NodeName).
    /// </summary>
    [Serializable]
    public class MissingRepairFactsException : Exception
    {
        /// <summary>
        /// Creates an instance of MissingRequiredFactsException.
        /// </summary>
        public MissingRepairFactsException()
        {
        }

        /// <summary>
        ///  Creates an instance of MissingRequiredFactsException.
        /// </summary>
        /// <param name="message">Error message that describes the problem.</param>
        public MissingRepairFactsException(string message) : base(message)
        {
        }

        /// <summary>
        /// Creates an instance of MissingRequiredFactsException.
        /// </summary>
        /// <param name="message">Error message that describes the problem.</param>
        /// <param name="innerException">InnerException instance.</param>
        public MissingRepairFactsException(string message, Exception innerException) : base(message, innerException)
        {
        }

    }
}