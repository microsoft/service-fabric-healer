// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer;
using FabricHealer.Interfaces;
using FabricHealer.SamplePlugins;
using Guan.Logic;
using FabricHealer.Utilities;

[assembly: RepairPredicateType(typeof(SampleRepairPredicateTypeStartup))]
namespace FabricHealer.SamplePlugins
{
    public class SampleRepairPredicateTypeStartup : IRepairPredicateType
    {
        public void RegisterToPredicateTypesCollection(FunctorTable functorTable, string serializedRepairData)
        {
            JsonSerializationUtility.TryDeserializeObject(serializedRepairData, out SampleTelemetryData repairData);
            functorTable.Add(SampleRepairPredicateType.Singleton("SampleRepair", repairData));
        }
    }
}