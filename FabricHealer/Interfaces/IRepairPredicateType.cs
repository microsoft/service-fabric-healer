using System;
using Guan.Logic;
using Microsoft.Extensions.DependencyInjection;

namespace FabricHealer.Interfaces
{
    public interface IRepairPredicateType
    {
        [Obsolete("This will be removed in a future release. Please use the LoadPredicateTypes() method instead.")]
        void RegisterToPredicateTypesCollection(FunctorTable functorTable, string serializedRepairData)
        {

        }

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
        void LoadPredicateTypes(IServiceCollection services);
    }
}
