using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;

namespace FabricHealer.Interfaces;

/// <summary>
/// Has to be implemented by the predicate types in the plugins, if EnableCustomRepairPredicateType and UsePluginModelV2 application parameters are set.
/// FH creates singleton instances of the implemented predicate classes.
/// </summary>
public interface IPredicateType
{
    /// <summary>
    /// Optional
    /// Has to be overridden if plugins need custom deserialization of the repair data.
    /// Will be executed during every repair workflows.
    /// By default, it will deserialize the repair data to <see cref="TelemetryData"/>.
    /// </summary>
    /// <typeparam name="T">Deserialization target type. A class that inherits from <see cref="TelemetryData"/></typeparam>
    /// <param name="json">Repair info present in the SF Health report</param>
    /// <returns>Deserialized repair data</returns>
    virtual T DeserializeRepairData<T>(string json) where T : TelemetryData
    {
        JsonSerializationUtility.TryDeserializeObject(json, out TelemetryData repairData);
        return (T)repairData;
    }

    /// <summary>
    /// Will be executed during every repair workflows.
    /// </summary>
    /// <typeparam name="T">Repair info type</typeparam>
    /// <param name="repairData">Repair info to be passed into the predicate</param>
    void SetRepairData<T>(T repairData) where T : TelemetryData;
}