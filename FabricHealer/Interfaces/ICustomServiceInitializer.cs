using System.Threading.Tasks;

namespace FabricHealer.Interfaces
{
    public interface ICustomServiceInitializer
    {
        Task InitializeAsync();
    }
}
