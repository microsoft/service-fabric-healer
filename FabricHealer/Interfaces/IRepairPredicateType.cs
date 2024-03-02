using FabricHealer.Utilities.Telemetry;
using Guan.Logic;

namespace FabricHealer.Interfaces
{
    public interface IRepairPredicateType
    {
        void RegisterToPredicateTypesCollection(FunctorTable functorTable, TelemetryData repairData);
    }
}
