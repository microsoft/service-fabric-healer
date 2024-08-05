using System;
using Microsoft.Extensions.DependencyInjection;

namespace FabricHealer.Interfaces
{
    /// <summary>
    /// Has to be implemented if either EnableCustomServiceInitializers or EnableCustomRepairPredicateType application parameters are set.
    /// The implementation should also use the attribute - <see cref="PluginAttribute"/>.
    /// FH creates singleton instances of the implemented plugin classes.
    /// </summary>
    public interface IPlugin
    {
        /// <summary>
        /// Has to be implemented if EnableCustomRepairPredicateType application parameter is set.
        /// Will be executed once at the beginning of the service.
        /// </summary>
        /// <param name="services">
        /// Service collection to register custom predicate types.
        /// Predicate types should inherit from <see cref="PredicateType"/> and implement <see cref="IPredicateType"/>
        /// Predicate types should be registered as singleton instances.
        /// </param>
        /// <returns>
        /// </returns>
        void RegisterPredicateTypes(IServiceCollection services)
        {
            throw new NotImplementedException("RegisterPredicateTypes must be implemented if EnableCustomRepairPredicateType application parameter is set.");
        }
    }
}
