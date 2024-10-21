using System;

namespace FabricHealer;

/// <summary>
/// FH plugin classes should use this attribute to be discovered by FH.
/// This attribute should be used instead of the CustomServiceInitializerAttribute and RepairPredicateTypeAttribute attributes.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public class PluginAttribute(Type pluginType) : Attribute
{
    public Type PluginType { get; } = pluginType;
}