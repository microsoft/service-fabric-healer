using System;
using FabricHealer.Interfaces;

namespace FabricHealer;

/// <summary>
/// Has to be used by the plugin assembly to mark the plugin type.
/// Used in combination with the <see cref="IPlugin"/> interface.
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