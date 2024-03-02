using FabricHealer.Utilities.Telemetry;
using Guan.Logic;

namespace FabricHealer.Interfaces
{
    public interface ICustomPredicateType
    {
        void RegisterToPredicateTypesCollection(FunctorTable functorTable, TelemetryData repairData);
    }
}
