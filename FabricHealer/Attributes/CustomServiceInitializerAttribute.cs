using System;

namespace FabricHealer
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class CustomServiceInitializerAttribute : Attribute
    {
        public Type InitializerType { get; }

        public CustomServiceInitializerAttribute(Type startupType)
        {
            this.InitializerType = startupType;
        }
    }
}
