// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Attributes;
using FabricHealer.Utilities;
using Microsoft.ServiceFabric.Services.Runtime;

namespace FabricHealer
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    public sealed class FabricHealer : StatelessService
    {
        private readonly Logger logger;

        public FabricHealer(StatelessServiceContext context)
                : base(context)
        {
            logger = new Logger(nameof(FabricHealer));
        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            await LoadCustomServiceInitializers();

            using FabricHealerManager healerManager = new(Context, cancellationToken);
            
            // Blocks until StartAsync exits.
            await healerManager.StartAsync();
        }

        private async Task LoadCustomServiceInitializers()
        {
            var pluginLoader = new CustomServiceInitializerPluginLoader(this.logger, this.Context);
            await pluginLoader.LoadPluginsAndCallCustomAction(typeof(CustomServiceInitializerAttribute), typeof(IServiceInitializer));
        }

        // Graceful close.
        protected override async Task OnCloseAsync(CancellationToken cancellationToken)
        {
            await FabricHealerManager.TryClearExistingHealthReportsAsync();
            await FabricHealerManager.TryCleanUpOrphanedFabricHealerRepairJobsAsync(isClosing: true);
            _ = base.OnCloseAsync(cancellationToken);
        }

        // Stateless replica restarted.
        protected override void OnAbort()
        {
            _ = FabricHealerManager.TryClearExistingHealthReportsAsync().Wait(15000);
            _ = FabricHealerManager.TryCleanUpOrphanedFabricHealerRepairJobsAsync(isClosing: true).Wait(15000);
            base.OnAbort();
        }
    }
}
