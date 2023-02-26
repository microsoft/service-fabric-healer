// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Guan.Logic;

namespace FabricHealer.Repair.Guan
{
    public class GuanQueryDispatcher
    {
        private readonly Module module_;

        public GuanQueryDispatcher(Module module)
        {
            module_ = module;
        }

        public async Task RunQueryAsync(string queryExpression)
        {
            ResolveOrder order = ResolveOrder.None;
            ModuleProvider moduleProvider = new();
            moduleProvider.Add(module_);
            QueryContext queryContext = new(moduleProvider);
            queryContext.SetDirection(null, order);
            Query query = Query.Create(queryExpression, queryContext);
            _ = await query.GetNextAsync();
        }

        public async Task RunQueryAsync(List<CompoundTerm> queryExpressions, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ResolveOrder order = ResolveOrder.None;
            ModuleProvider moduleProvider = new();
            moduleProvider.Add(module_);
            QueryContext queryContext = new(moduleProvider);
            queryContext.SetDirection(null, order);
            Query query = Query.Create(queryExpressions, queryContext, moduleProvider);
            _ = await query.GetNextAsync();
        }
    }
}
