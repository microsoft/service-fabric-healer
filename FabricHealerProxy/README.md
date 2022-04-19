# FabricHealerLib

FabricHealerLib is a .NET Standard 2.0 library that provides a very simple and reliable way to share Service Fabric entity repair information to FabricHealer service instances running in the same cluster. You can install FabricHealerLib into your .NET Service Fabric service from the [nuget.org package gallery](...). 

### How to use FabricHealerLib

- Deploy [FabricHealer](https://github.com/microsoft/service-fabric-healer/releases) [TODO: this will point to Deployment doc folder] to your cluster (Do note that if you deploy FabricHealer as a singleton partition 1 (versus -1), then FH will only conduct SF-related repairs).
- Install FabricHealerLib nupkg into your own service from where you want to initiate repair of SF entities (stateful/stateless services, Fabric nodes).

FabricHealer will execute entity-related logic rules (housed in it's FabricNodeRules.guan file in this case), and if any of the rules succeed, then FH will create a Repair Job with pre and post safety checks (default),
orchestrate RM through to repair completion (FH will be the executor of the repair), emit repair step information via telemetry, local logging, and etw.

### Sample application (Stateless Service)

stateless1.cs 

```C#
using System;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Services.Runtime;
using FabricHealerLib;
using FabricHealerLib.Exceptions;

namespace Stateless1
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    internal sealed class Stateless1 : StatelessService
    {
        public Stateless1(StatelessServiceContext context)
            : base(context)
        {

        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            // This specifies that you want FabricHealer to repair a service instance deployed to a Fabric node named NodeName.
            // FabricHealer supports both Replica and CodePackage restarts of services. The logic rules will dictate which one of these happens,
            // so make sure to craft a specific logic rule that makes sense for you (and use some logic!).
            // Note that, out of the box, FabricHealer's AppRules.guan file already has a restart replica catch-all (applies to any service) rule that will restart the primary replica of
            // the specified service below, deployed to the a specified Fabric node. 
            var repairDataServiceTarget = new RepairData
            {
                ServiceName = "fabric:/HealthMetrics/DoctorActorServiceType",
                NodeName = "_Node_0"
            };

            // This specifies that you want FabricHealer to repair a Fabric node named NodeName. The only supported repair in FabricHealer is a Restart.
            // So, implicitly, this means you want FabricHealer to restart _Node_0.
            var repairDataNodeTarget = new RepairData
            {
                NodeName = "_Node_0"
            };

            // In this case, you must place this using declaration of FabricHealerProxy instance at function scope (so, not within the try below).
            // Failure to do so will result in nothing happening as the FabricClient instance that FabricHealerProxy creates will have closed before
            // Service Fabric's HealthManager has completed its related work.
            

            // Service repair.
            try
            {
                await FabricHealerProxy.RepairEntityAsync(repairDataServiceTarget, cancellationToken, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            }
            catch (MissingRequiredDataException)
            {
                // This means a required RepairData property was not specified. For example, RepairData.NodeName was not set.
            }
            catch (FabricNodeNotFoundException)
            {
                // The Fabric node you specified in RepairData.NodeName does not exist.
            }
            catch (FabricServiceNotFoundException)
            {
                // The Fabric service you specified in RepairData.ServiceName does not exist.
            }
            catch (FabricException)
            {
                // Thrown when an internal Service Fabric operation fails. Internally, RepairEntityAsync will retry failed Fabric client operations 3 times.
                // This will have already lead to 3 internal retries before surfacing here.
            }
            catch (TimeoutException)
            {
                // Thrown when a Fabric client API call times out. This will have already lead to 3 internal retries before surfacing here.
                // ClusterManager service could be hammered (flooded with queries), for example. You could retry RepairEntityAsync again after you wait a bit..
            }

            // Node repair.
            try
            {
                await FabricHealerProxy.RepairEntityAsync(repairDataNodeTarget, cancellationToken, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            }
            catch (FabricNodeNotFoundException)
            {
                // Check your spelling..
            }
            catch (FabricException)
            {
                // No-op unless you want to re-run RepairEntityAsync again.
            }
            catch (TimeoutException)
            {
                // ClusterManager service could be hammered (flooded with queries), for example. You could retry RepairEntityAsync again after you wait a bit..
            }

            // Do nothing and wait.
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
        }
    }
}
```

