using System;
using System.Threading.Tasks;

namespace FabricHealer.Interfaces
{
    [Obsolete("This will be removed in a future release. Please use the IPlugin interface instead.")]
    public interface ICustomServiceInitializer
    {
        Task InitializeAsync();
    }
}
