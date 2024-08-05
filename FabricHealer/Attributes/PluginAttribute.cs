using System;

namespace FabricHealer;

/// <summary>
/// Has to be used by the plugin assembly to mark the plugin type.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public class PluginAttribute : Attribute
{
    public Type PluginType { get; }

    public PluginAttribute(Type pluginType)
    {
        this.PluginType = pluginType;
    }
}