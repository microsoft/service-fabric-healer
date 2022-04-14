using System;

namespace FabricHealerLib
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
    }
}
