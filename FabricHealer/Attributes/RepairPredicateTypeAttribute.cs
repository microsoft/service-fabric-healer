using System;

namespace FabricHealer.Attributes
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class RepairPredicateTypeAttribute : Attribute
    {
        public Type CustomRepairPredicateType { get; }

        public RepairPredicateTypeAttribute(Type className)
        {
            CustomRepairPredicateType = className;
        }
    }
}
