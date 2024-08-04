using System;
using Guan.Logic;

namespace FabricHealer.Interfaces
{
    [Obsolete("This will be removed in a future release. Please use the IPlugin and Ipredicate interfaces instead.")]
    public interface IRepairPredicateType
    {
        void RegisterToPredicateTypesCollection(FunctorTable functorTable, string serializedRepairData);
    }
}
