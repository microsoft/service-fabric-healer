using System;

namespace FabricHealer
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class RepairPredicateTypeAttribute(Type className) : Attribute
    {
        public Type CustomRepairPredicateType { get; } = className;
    }
}
