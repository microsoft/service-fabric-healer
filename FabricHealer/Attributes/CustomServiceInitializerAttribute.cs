using System;

namespace FabricHealer
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class CustomServiceInitializerAttribute(Type startupType) : Attribute
    {
        public Type InitializerType { get; } = startupType;
    }
}
