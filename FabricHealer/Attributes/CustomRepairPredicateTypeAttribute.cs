using System;

namespace FabricHealer.Attributes
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class CustomRepairPredicateTypeAttribute : Attribute
    {
        public Type CustomRepairPredicateType { get; }

        public CustomRepairPredicateTypeAttribute(Type className)
        {
            CustomRepairPredicateType = className;
        }
    }
}
