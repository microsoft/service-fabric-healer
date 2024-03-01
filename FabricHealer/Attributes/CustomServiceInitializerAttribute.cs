using System;

namespace FabricHealer.Attributes
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class CustomServiceInitializerAttribute : Attribute
    {
        public Type InitializerType { get; }

        public CustomServiceInitializerAttribute(Type startupType)
        {
            InitializerType = startupType;
        }
    }
}
