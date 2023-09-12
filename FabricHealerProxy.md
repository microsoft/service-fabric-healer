# FabricHealerProxy 1.0.4

FabricHealerProxy is a .NET 6 library that provides a very simple and reliable way for any .NET Service Fabric service to initiate Service Fabric entity repair by the FabricHealer service running in the same cluster. It is assumed that you have experience with FabricHealer and understand how to use it.

### How to use FabricHealerProxy

- Learn how to use [FabricHealer](https://github.com/microsoft/service-fabric-healer) and get comfortable with its [Configuration as Logic model](https://github.com/microsoft/service-fabric-healer/blob/main/Documentation/LogicWorkflows.md).
- [Deploy FabricHealer](https://github.com/microsoft/service-fabric-healer#deploy-fabrichealer) to your Service Fabric cluster.
- Install FabricHealerProxy into your own .NET service and write a small amount of code to initiate FabricHealer repairs for a variety of targets. 

The API is *very* simple, by design. For example, this is how you would initiate a repair for a service running on a specified Fabric node:

```C#
var serviceRepairFacts = new RepairFacts
{
    ServiceName = "fabric:/GettingStartedApplication/MyActorService",
    NodeName = "appnode4"
};

await FabricHealerProxy.Instance.RepairEntityAsync(serviceRepairFacts, cancellationToken);
```

FabricHealerProxy will use the information you provide - even when it is as terse as above - to generate all the facts that FabricHealer needs to successfully execute the related entity-specific repair as defined in logic rules. If any of the related logic rules succeed,
then FH will orchestrate Service Fabric's RepairManager service through to repair job completion, emitting repair step information via telemetry, local logging, and etw along the way.
The above sample is *all* that is needed to restart a service running on a Fabric node named appnode4, for example. Of course, it depends on what the FabricHealer logic dictates, but you define that in FabricHealer with user configuration - that also happens to be logic programming :)