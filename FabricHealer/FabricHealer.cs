// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

//using System.Collections.Generic;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
//using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
//using FabricHealer.Interfaces;
//using FabricHealer.Utilities;
//using Microsoft.ServiceFabric.Services.Remoting.V2.FabricTransport.Runtime;
//using FabricHealer.Utilities.Telemetry;

namespace FabricHealer
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    public sealed class FabricHealer : StatelessService//, IProcessHealthData
    {
        private FabricHealerManager healerManager;

        public FabricHealer(StatelessServiceContext context)
            : base(context)
        {
        }

        // This function is called by FabricObserver instances running in the same cluster as FH.
        // This is done over RPC with Service Fabric Remoting V2.
        /*public Task<string> ProcessHealthTelemetry(string telemetryData)
        {
            if (!JsonHelper.IsJson<TelemetryData>(telemetryData))
            {
                return Task.FromResult("Status: Failed. Incorrect format. You must send Json.");
            }

            if (!SerializationUtility.TryDeserialize(telemetryData, out TelemetryData foHealthData))
            {
                return Task.FromResult("Status: Failed. Supplied Json is not TelemetryData.");
            }

            // Do not await this or get result. The caller is a different service running in the same cluster,
            // FabricObserver, and doesn't need to wait for this task to complete (which will take some time).
            _ = this.healerManager.ProcessRPCHealthDataAsync(foHealthData);

            return Task.FromResult("Status: Successful. TelemetryData received.");
        }*/

        /// <summary>
        /// Optional override to create listeners (e.g., TCP, HTTP) for this service replica to handle client or user requests.
        /// </summary>
        /// <returns>A collection of listeners.</returns>
        /*protected override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
        {
            return new[]
            {
                new ServiceReplicaListener(
                    (context) => new FabricTransportServiceRemotingListener(context,this))
            };
        }*/

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
