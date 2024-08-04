using System;

namespace FabricHealer
{
    [Obsolete("This will be removed in a future release. Please use the Plugin attribute instead.")]
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class RepairPredicateTypeAttribute : Attribute
    {
        public Type CustomRepairPredicateType { get; }

        public RepairPredicateTypeAttribute(Type className)
        {
            this.CustomRepairPredicateType = className;
        }
    }
}
