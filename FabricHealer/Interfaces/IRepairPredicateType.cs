using Guan.Logic;
using System;
using System.Collections.Generic;

namespace FabricHealer.Interfaces
{
    public interface IRepairPredicateType
    {
        void RegisterToPredicateTypesCollection(FunctorTable functorTable, string serializedRepairData);
    }

    /// <summary>
    /// Clients who want to disable plugin load during every repair run, have to implement this interface.
    /// </summary>
    public interface IRepairPredicateTypeV2
    {
        Dictionary<string, Type> GetPredicateTypes();
    }
}
