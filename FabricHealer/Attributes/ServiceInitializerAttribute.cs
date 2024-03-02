using System;

namespace FabricHealer.Attributes
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class ServiceInitializerAttribute : Attribute
    {
        public Type InitializerType { get; }

        public ServiceInitializerAttribute(Type startupType)
        {
            InitializerType = startupType;
        }
    }
}
