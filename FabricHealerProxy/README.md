# FabricHealerProxy

FabricHealerProxy is a .NET Standard 2.0 library that provides a very simple and reliable way for any .NET Service Fabric service to initiate Service Fabric entity repair by the FabricHealer service running in the same cluster. You can install FabricHealerProxy into your .NET Service Fabric service from the [nuget.org package gallery](...). 

### How to use FabricHealerProxy

- Deploy [FabricHealer](https://github.com/microsoft/service-fabric-healer/releases) [TODO: this will point to Deployment doc folder] to your cluster (Do note that if you deploy FabricHealer as a singleton partition 1 (versus -1), then FH will only conduct SF-related repairs).
- Install FabricHealerProxy nupkg into your own service from where you want to initiate repair of SF entities (stateful/stateless services, Fabric nodes).

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
using FabricHealerProxy;
using FabricHealerProxy.Exceptions;
using System.Collections.Generic;

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
            // Note that, out of the box, FabricHealer's AppRules.guan file (located in the FabricHealer project's PackageRoot/Config/LogicRules folder)
            // already has a restart replica catch-all (applies to any service) rule that will restart the primary replica of
            // the specified service below, deployed to the a specified Fabric node. 
            var repairDataServiceTarget = new RepairData
            {
                ServiceName = "fabric:/HealthMetrics/DoctorActorServiceType",
                NodeName = "_Node_0"
            };

            var repairDataServiceTarget2 = new RepairData
            {
                ServiceName = "fabric:/HealthMetrics/BandActorServiceType",
                NodeName = "_Node_0"
            };

            // This specifies that you want FabricHealer to repair a Fabric node named NodeName. The only supported repair in FabricHealer is a Restart.
            // Related rules can be found in FabricNodeRepair.guan file in the FabricHealer project's PackageRoot/Config/LogicRules folder.
            // So, implicitly, this means you want FabricHealer to restart _Node_0. You can of course modify the related logic rules to do something else. It's up to you!
            var repairDataNodeTarget = new RepairData
            {
                NodeName = "_Node_0"
            };

            // For use in the IEnumerable<RepairData> RepairEntityAsync overload.
            List<RepairData> repairDataList = new List<RepairData>
            {
                repairDataNodeTarget,
                repairDataServiceTarget,
                repairDataServiceTarget2
            };

            // For use in the single instance RepairData RepairEntityAsync overload.
            var repairDataServiceTargetSingle = new RepairData
            {
                ServiceName = "fabric:/HealthMetrics/HealthMetrics.WebServiceType",
                NodeName = "_Node_0"
            };

            // This demonstrates which exceptions will be thrown by the API. The first three represent user error (most likely). The last two are internal SF issues which 
            // will be thrown only after a series of retries. How to handle these is up to you.
            try
            {
                await FabricHealer.Proxy.RepairEntityAsync(repairDataServiceTargetSingle, cancellationToken, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                await FabricHealer.Proxy.RepairEntityAsync(repairDataList, cancellationToken, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            }
            catch (MissingRepairDataException)
            {
                // This means a required non-null value for a RepairData property was not specified. For example, RepairData.NodeName was not set.
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

            // Do nothing and wait.
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                }
                catch (TaskCanceledException)
                {

                }
            }

            // Close the proxy (this cleans up state and removes any health report that is currently active (not expired).
            // Note: this does not cancel repairs that are in flight or in the FabricHealer internal repair queue.
            await FabricHealer.Proxy.Close();
        }
    }
}
```

