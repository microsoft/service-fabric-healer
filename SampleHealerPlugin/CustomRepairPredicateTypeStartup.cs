// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Attributes;
using FabricHealer.Interfaces;
using FabricHealer.Repair.Guan;
using Guan.Logic;
using FabricHealer.Utilities.Telemetry;

[assembly: CustomRepairPredicateType(typeof(CustomRepairPredicateTypeStartup))]
namespace FabricHealer.Repair.Guan
{
    public class CustomRepairPredicateTypeStartup : ICustomPredicateType
    {
        public void RegisterToPredicateTypesCollection(FunctorTable functorTable, TelemetryData repairData)
        {
            functorTable.Add(CustomRepairPredicateType.Singleton("CustomRepair", repairData));
        }
    }
}