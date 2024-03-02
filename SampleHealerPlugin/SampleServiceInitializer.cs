using FabricHealer.SamplePlugins;
using FabricHealer.Attributes;
using FabricHealer.Interfaces;

[assembly: ServiceInitializer(typeof(SampleServiceInitializer))]
namespace FabricHealer.SamplePlugins
{
    public class SampleServiceInitializer : IServiceInitializer
    {
        public Task InitializeAsync()
        {
            return Task.CompletedTask;
        }
    }
}
