// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer;
using FabricHealer.Interfaces;
using FabricHealer.SamplePlugins;
using Guan.Logic;
using Microsoft.Extensions.DependencyInjection;


[assembly: Plugin(typeof(SampleRepairPredicateTypeStartup))]
namespace FabricHealer.SamplePlugins
{
    public class SampleRepairPredicateTypeStartup : IRepairPredicateType
    {
        public void LoadPredicateTypes(IServiceCollection services)
        {
            services.AddSingleton<PredicateType>(new SampleRepairPredicateType("SampleRepair"));
        }
    }
}