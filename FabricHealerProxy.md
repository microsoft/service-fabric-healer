# FabricHealerProxy

FabricHealerProxy is a .NET Standard 2.0 library that provides a very simple and reliable way for any .NET Service Fabric service to initiate Service Fabric entity repair by the FabricHealer service running in the same cluster. 

### How to use FabricHealerProxy

- Deploy [FabricHealer](https://github.com/microsoft/service-fabric-healer/releases) to your cluster (Do note that if you deploy FabricHealer as a singleton partition 1 (versus -1), then FH will only conduct SF-related repairs).
- Install FabricHealerProxy nupkg [TODO: Link to nuget.org section] into your own service from where you want to initiate repair of SF entities (stateful/stateless services, Fabric nodes).

FabricHealer will execute entity-related logic rules (housed in FabricHealer's PackageRoot/Config/LogicRules folder), and if any of the related rules succeed, then FH will create a Repair Job with pre and post safety checks (default),
orchestrate RM through to repair completion (FH will be the executor of the repair), emit repair step information via telemetry, local logging, and etw.

### Sample application (Stateless Service)

Stateless1.cs 

```C#
using System;
using System.Collections.Generic;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Services.Runtime;
using FabricHealerProxy;

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
            // Note that, out of the box, FabricHealer's AppRules.guan file located in the FabricHealer project's PackageRoot/Config/LogicRules folder
            // already has a restart replica catch-all (applies to any service) rule that will restart the primary replica of
            // the specified service below, deployed to the a specified Fabric node. 
            // By default, if you only supply NodeName and ServiceName, then FabricHealerProxy assumes the target EntityType is Service. This is a convience to limit how many facts
            // you must supply in a RepairFacts instance. Note that for *any* type of repair, NodeName is always required.
            var RepairFactsServiceTarget1 = new RepairFacts
            {
                ServiceName = "fabric:/GettingStartedApplication/MyActorService",
                NodeName = "_Node_0"
            };

            var RepairFactsServiceTarget2 = new RepairFacts
            {
                ServiceName = "fabric:/GettingStartedApplication/StatefulBackendService",
                NodeName = "_Node_0"
            };

            var RepairFactsServiceTarget3 = new RepairFacts
            {
                ServiceName = "fabric:/GettingStartedApplication/StatelessBackendService",
                NodeName = "_Node_0"
            };

            var RepairFactsServiceTarget4 = new RepairFacts
            {
                ServiceName = "fabric:/BadApp/BadService",
                NodeName = "_Node_0"
            };

            var RepairFactsServiceTarget5 = new RepairFacts
            {
                ServiceName = "fabric:/Voting/VotingData",
                NodeName = "_Node_0"
            };

            var RepairFactsServiceTarget6 = new RepairFacts
            {
                ServiceName = "fabric:/Voting/VotingWeb",
                NodeName = "_Node_0"
            };

            var RepairFactsServiceTarget7 = new RepairFacts
            {
                ServiceName = "fabric:/GettingStartedApplication/WebService",
                NodeName = "_Node_0"
            };

            // This specifies that you want FabricHealer to repair a Fabric node named _Node_0. The only supported Fabric node repair in FabricHealer is a Restart.
            // Related rules can be found in FabricNodeRules.guan file in the FabricHealer project's PackageRoot/Config/LogicRules folder.
            // So, implicitly, this means you want FabricHealer to restart _Node_0. By default, if you only supply NodeName, then FabricHealerProxy assumes the target EntityType is Node.
            var RepairFactsNodeTarget = new RepairFacts
            {
                NodeName = "_Node_0"
            };

            // Initiate a reboot of the machine hosting the specified Fabric node, _Node_4. This will be executed by the InfrastructureService for the related node type.
            // The related logic rules for this repair target are housed in FabricHealer's MachineRules.guan file.
            var RepairFactsMachineTarget = new RepairFacts
            {
                NodeName = "_Node_0",
                EntityType = EntityType.Machine
            };

            // Restart system service process.
            var SystemServiceRepairFacts = new RepairFacts
            {
                ApplicationName = "fabric:/System",
                NodeName = "_Node_0",
                SystemServiceProcessName = "FabricDCA",
                ProcessId = 73588,
                Code = SupportedErrorCodes.AppWarningMemoryMB
            };

            // Disk - Delete files. This only works if FabricHealer instance is present on the same target node.
            // Note the rules in FabricHealer\PackageRoot\LogicRules\DiskRules.guan file in the FabricHealer project.
            var DiskRepairFacts = new RepairFacts
            {
                NodeName = "_Node_0",
                EntityType = EntityType.Disk,
                Metric = SupportedMetricNames.DiskSpaceUsageMb,
                Code = SupportedErrorCodes.NodeWarningDiskSpaceMB
            };

            // For use in the IEnumerable<RepairFacts> RepairEntityAsync overload.
            List<RepairFacts> RepairFactsList = new List<RepairFacts>
            {
                RepairFactsNodeTarget,
                RepairFactsServiceTarget1,
                RepairFactsServiceTarget2,
                RepairFactsServiceTarget3,
                RepairFactsServiceTarget4,
                RepairFactsServiceTarget5,
                RepairFactsServiceTarget6,
                RepairFactsServiceTarget7
            };

            // This demonstrates which exceptions will be thrown by the API. The first three are FabricHealerProxy custom exceptions and represent user error (most likely).
            // The last two are internal SF issues which will be thrown only after a series of retries. How to handle these is up to you.
            try
            {
                await FabricHealer.Proxy.RepairEntityAsync(DiskRepairFacts, cancellationToken);
                //await FabricHealer.Proxy.RepairEntityAsync(SystemServiceRepairFacts, cancellationToken);
                //await FabricHealer.Proxy.RepairEntityAsync(RepairFactsMachineTarget, cancellationToken);
                //await FabricHealer.Proxy.RepairEntityAsync(RepairFactsList, cancellationToken);
            }
            catch (MissingRepairFactsException)
            {
                // This means a required non-null value for a RepairFacts property was not specified. For example, RepairFacts.NodeName was not set.
                // Any instance of RepairFacts must contain a value for NodeName.
                // If you catch this exception, then the idea is that you would do something about it here. 
            }
            catch (NodeNotFoundException)
            {
                // The Fabric node you specified in RepairFacts.NodeName does not exist.
                // If you catch this exception, then the idea is that you would do something about it here. 
            }
            catch (ServiceNotFoundException)
            {
                // The Fabric service you specified in RepairFacts.ServiceName does not exist.
                // If you catch this exception, then the idea is that you would do something about it here. 
            }
            catch (FabricException)
            {
                // Thrown when an internal Service Fabric operation fails. Internally, RepairEntityAsync will retry failed Fabric client operations 4 times at increasing wait intervals.
                // This means that something is wrong at the SF level, so you could wait and then try again later.
            }
            catch (TimeoutException)
            {
                // Thrown when a Fabric client API call times out. This will have already lead to 4 internal retries before surfacing here.
                // This means that something is wrong at the SF level, so you could wait and then try again later.
            }

            // FabricHealerProxy API is thread-safe. So, you could also process the List<RepairFacts> above in a parallel loop, for example.
            /*

            _ = Parallel.For (0, RepairFactsList.Count, async (i, state) =>
            {
                await FabricHealer.Proxy.RepairEntityAsync(RepairFactsList[i], cancellationToken).ConfigureAwait(false);
            });
            
            */

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

            // When the RunAsync cancellationToken is cancelled (in this case by the SF runtime) any active health reports will be automatically cleared by FabricHealerProxy.
            // Note: This does not guarantee that some target entity that has an active FabricHealerProxy health report will be cancelled. Cancellation of repairs is
            // not currently supported by FabricHealer.
        } 
    }
}
```