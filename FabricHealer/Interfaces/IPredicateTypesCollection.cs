using FabricHealer.Utilities.Telemetry;
using Guan.Logic;

namespace FabricHealer.Interfaces
{
    public interface IPredicateTypesCollection
    {
        void RegisterPredicateTypes(FunctorTable functorTable, TelemetryData repairData);
    }
}
