// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Attributes;
using FabricHealer.Interfaces;
using FabricHealer.SamplePlugins;
using Guan.Logic;
using FabricHealer.Utilities.Telemetry;

[assembly: RepairPredicateType(typeof(SampleRepairPredicateTypeStartup))]
namespace FabricHealer.SamplePlugins
{
    public class SampleRepairPredicateTypeStartup : IRepairPredicateType
    {
        public void RegisterToPredicateTypesCollection(FunctorTable functorTable, TelemetryData repairData)
        {
            functorTable.Add(SampleRepairPredicateType.Singleton("CustomRepair", repairData));
        }
    }
}