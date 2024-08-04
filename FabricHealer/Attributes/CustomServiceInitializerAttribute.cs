using System;

namespace FabricHealer
{
    [Obsolete("This will be removed in a future release. Please use the Plugin attribute instead.")]
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
