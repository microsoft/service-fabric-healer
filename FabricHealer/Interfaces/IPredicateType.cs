namespace FabricHealer.Interfaces;

/// <summary>
/// Has to be implemented by the predicate types in the plugins, if EnableCustomRepairPredicateType and UsePluginModelV2 application parameters are set.
/// FH creates singleton instances of the implemented predicate classes.
/// </summary>
public interface IPredicateType
{
    /// <summary>
    /// Will be executed during every repair workflows.
    /// </summary>
    /// <typeparam name="T">Repair info type</typeparam>
    /// <param name="repairData">Repair info to be passed into the predicate</param>
    void SetRepairData(string serializedRepairData);
}