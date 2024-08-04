using System.Fabric;
using FabricHealer;
using FabricHealer.Interfaces;
using FabricHealer.SamplePlugins;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using SampleHealerPlugin;

[assembly: Plugin(typeof(SamplePlugin))]
namespace SampleHealerPlugin
{
    /// <summary>
    /// Sample plugin for FabricHealer.
    ///  Implemented using the new Fabric healer plugin contracts.
    /// </summary>
    internal class SamplePlugin : IPlugin
    {
        public Task InitializeAsync(ServiceContext serviceContext, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// sample plugin implements this method to deserialize the repair data to a custom type.
        public T DeserializeRepairData<T>(string json) where T : TelemetryData
        {
            JsonSerializationUtility.TryDeserializeObject(json, out SampleTelemetryData repairData);
            return repairData as T;
        }

        public IReadOnlyDictionary<string, Type> GetPredicateTypes()
        {
            return new Dictionary<string, Type>
            {
                { "SampleRepairV2", typeof(SamplePredicateType) }
            };
        }
    }
}
