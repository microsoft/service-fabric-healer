using System;
using System.Collections.Generic;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;

namespace FabricHealer.Interfaces
{
    /// <summary>
    /// Has to be implemented if either EnableCustomServiceInitializers or EnableCustomRepairPredicateType application parameters are set.
    /// The implementation should also use the attribute - <see cref="PluginAttribute"/>.
    /// FH creates singleton instances of the implemented plugin classes.
    /// </summary>
    public interface IPlugin
    {
        /// <summary>
        /// Has to be implemented if EnableCustomServiceInitializers application parameter is set.
        /// Will be executed once at the beginning of the service.
        /// </summary>
        /// <param name="serviceContext">Fabric healer's service context</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns></returns>
        Task InitializeAsync(ServiceContext serviceContext, CancellationToken cancellationToken);

        /// <summary>
        /// Optional
        /// Has to be overridden if plugins need custom deserialization of the repair data.
        /// Will be executed during every repair workflows if EnableCustomRepairPredicateType application parameter is set.
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
        /// Has to be implemented if EnableCustomRepairPredicateType application parameter is set.
        /// Will be executed once at the beginning of the service.
        /// </summary>
        /// <returns>
        /// A map of predicate names and their respective types.
        /// types should be classes that inherit from <see cref="IPredicate"/> and <see cref="PredicateType"/>
        /// </returns>
        IReadOnlyDictionary<string, Type> GetPredicateTypes();
    }
}
