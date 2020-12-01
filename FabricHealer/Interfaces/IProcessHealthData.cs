using Microsoft.ServiceFabric.Services.Remoting;
using System.Threading.Tasks;

namespace FabricHealer.Interfaces
{ 
    public interface IProcessHealthData : IService
    {
        Task<string> ProcessHealthTelemetry(string telemetryData);
    }
}
