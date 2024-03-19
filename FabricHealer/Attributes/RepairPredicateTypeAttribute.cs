using System;

namespace FabricHealer
{
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
