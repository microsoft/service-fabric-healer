// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
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
            // FabricHealerManager will create a cancellation token object (see Token instance member) that is this RunAsync cancellation token, which is
            // threaded through all async operations throughout the program. FabricHealerManager.Instance singleton ensures that only one instance of FH can be constructed.
            // The FabricHealerManager ctor is private.
            using var healerManager = FabricHealerManager.Instance(Context, cancellationToken);

            // Blocks until StartAsync completes. If RM is not deployed, for example, StartAsync will complete immediately.
            await healerManager.StartAsync();

            try
            {
                // Blocks until cancellation token is cancelled to prevent exiting RunAsync in the absence of RM being deployed.
                await Task.Delay(-1, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                // Do nothing.
            }

            // exit RunAsync.
        }

        // CTRL-C (graceful shutdown).
        protected override async Task OnCloseAsync(CancellationToken token)
        {
            try
            {
                await FabricHealerManager.TryCleanUpOrphanedFabricHealerRepairJobsAsync(true);
                await FabricHealerManager.TryClearExistingHealthReportsAsync();
            }
            catch (Exception e) when (e is ArgumentException or AggregateException or FabricException or ObjectDisposedException)
            {

            }

            _ = base.OnCloseAsync(token);
        }

        // FH Replica instance deleted.
        protected override void OnAbort()
        {
            try
            {
                FabricHealerManager.TryCleanUpOrphanedFabricHealerRepairJobsAsync(true).Wait(TimeSpan.FromSeconds(30));
                FabricHealerManager.TryClearExistingHealthReportsAsync().Wait(TimeSpan.FromSeconds(30));
            }
            catch (Exception e) when (e is ArgumentException or AggregateException or FabricException or ObjectDisposedException)
            {

            }

            base.OnAbort();
        }
    }
}
