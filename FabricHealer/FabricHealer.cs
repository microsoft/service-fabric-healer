// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Services.Runtime;

namespace FabricHealer
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    public sealed class FabricHealer : StatelessService
    {
        private FabricHealerManager healerManager;

        public FabricHealer(StatelessServiceContext context)
            : base(context)
        {
        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            // FabricHealerManager will create an instance member cancellation token object (see Token) that is this cancellation token,
            // which is threaded through all async operations throughout the program.
            healerManager = FabricHealerManager.Singleton(Context, cancellationToken);
            
            // Blocks until cancellationToken cancellation.
            await healerManager.StartAsync().ConfigureAwait(true);
        }
    }
}
