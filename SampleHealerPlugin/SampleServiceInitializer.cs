using FabricHealer.SamplePlugins;
using FabricHealer.Interfaces;
using FabricHealer;

[assembly: CustomServiceInitializer(typeof(SampleServiceInitializer))]
namespace FabricHealer.SamplePlugins
{
    public class SampleServiceInitializer : ICustomServiceInitializer
    {
        public Task InitializeAsync()
        {
            return Task.CompletedTask;
        }
    }
}
