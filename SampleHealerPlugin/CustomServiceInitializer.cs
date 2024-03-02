using FabricHealer.SamplePlugins;
using FabricHealer.Attributes;
using FabricHealer.Interfaces;

[assembly: CustomServiceInitializer(typeof(CustomServiceInitializer))]
namespace FabricHealer.SamplePlugins
{
    public class CustomServiceInitializer : ICustomServiceInitializer
    {
        public Task InitializeAsync()
        {
            return Task.CompletedTask;
        }
    }
}
