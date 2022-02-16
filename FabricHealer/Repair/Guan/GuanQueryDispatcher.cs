// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Guan.Logic
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
            ModuleProvider moduleProvider = new ModuleProvider();
            moduleProvider.Add(module_);
            QueryContext queryContext = new QueryContext(moduleProvider);
            queryContext.SetDirection(null, order);
            Query query = Query.Create(queryExpression, queryContext);
            await query.GetNextAsync().ConfigureAwait(false);
        }

        public async Task RunQueryAsync(List<CompoundTerm> queryExpressions)
        {
            ResolveOrder order = ResolveOrder.None;
            ModuleProvider moduleProvider = new ModuleProvider();
            moduleProvider.Add(module_);
            QueryContext queryContext = new QueryContext(moduleProvider);
            queryContext.SetDirection(null, order);
            Query query = Query.Create(queryExpressions, queryContext, moduleProvider);
            await query.GetNextAsync().ConfigureAwait(false);
        }
    }
}
