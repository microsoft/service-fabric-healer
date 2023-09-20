## FabricHealer 1.2.8
### Configuration as Logic and auto-mitigation in Service Fabric clusters

FabricHealer (FH) is a .NET 6 Service Fabric application that attempts to automatically fix a set of reliably solvable problems that can take place in Service Fabric
applications (including containers), host virtual machines, and logical disks (scoped to space usage problems only). These repairs mostly employ a set of Service Fabric API calls,
but can also be fully customizable (like Disk repair). All repairs are safely orchestrated through the Service Fabric RepairManager system service.
Repair workflow configuration is written as [Prolog](http://www.let.rug.nl/bos/lpn/lpnpage.php?pageid=online)-like [logic](https://github.com/microsoft/service-fabric-healer/blob/main/FabricHealer/PackageRoot/Config/LogicRules) with [supporting external predicates](https://github.com/microsoft/service-fabric-healer/blob/main/FabricHealer/Repair/Guan) written in C#. 

FabricHealer's Configuration-as-Logic feature requires [Guan](https://github.com/microsoft/guan), a Prolog-like logic programming library for .NET.
Repair workflow starts when FabricHealer detects supported error or warning health events reported by [FabricObserver](https://github.com/microsoft/service-fabric-observer) or [FabricHealerProxy](https://www.nuget.org/packages/Microsoft.ServiceFabricApps.FabricHealerProxy), for example.
Note that you can use FabricHealer if you don't also employ FabricObserver or FabricHealerProxy. For [machine-level repairs](https://github.com/microsoft/service-fabric-healer/blob/develop/FabricHealer/PackageRoot/Config/LogicRules/MachineRules.guan) you do not need either of these if you want to automatically schedule machine repair jobs based on node health states alone (like, Error state, specifically). For all other repairs, you must install FabricHealerProxy into a .NET Service Fabric project to leverage the power of FabricHealer if you do not deploy FabricObserver. 

```
FabricObserver and FabricHealer work great together.
```

FabricHealer is implemented as a stateless singleton service that runs on one or all nodes in a Linux or Windows Service Fabric cluster. For Disk and Fabric system service repairs, you must run FabricHealer on all nodes.
FabricHealer is built as a .NET 6.0 application and has been tested on multiple versions of Windows Server and Ubuntu.  

To learn more about FabricHealer's configuration-as-logic model, [click here.](https://github.com/microsoft/service-fabric-healer/blob/main/Documentation/LogicWorkflows.md)  


```
FabricHealer requires SF Runtime versions 9 and higher.
```
```
FabricHealer requires that RepairManager (RM) service is deployed. 
```
```
For VM level repair, InfrastructureService (IS) service must be deployed.
```

## Build and run  

1. Clone the repo.
2. Install [.NET 6](https://dotnet.microsoft.com/download/dotnet-core/6.0)
3. Build. 

## Using FabricHealer  

```
FabricHealer is a service specifically designed to auto-mitigate Service Fabric service issues that are generally 
the result of bugs in user code.
```  

Let's say you have a service that is using too much memory or too many ephemeral ports, as defined in both FabricObserver (which generates the Warning(s)) and in your related logic rule (this is optional since you can decide that if FabricObserver warns, then FabricHealer should mitigate without testing the related metric value that led to the Warning by FabricObserver, which, of course, you configured. It's up to you.). You would use FabricHealer to keep the problem in check while your developers figure out the root cause and fix the bug(s) that lead to resource usage over-consumption. FabricHealer is really just a temporary solution to problems, not a fix. This is how you should think about auto-mitigation, generally. FabricHealer aims to keep your cluster green while you fix your bugs. With it's configuration-as-logic support, you can easily specify that some repair for some service should only be attempted for n weeks or months, while your dev team fixes the underlying issues with the problematic service. FabricHealer should be thought of as a "disappearing task force" in that it can provide stability during times of instability, then "go away" when bugs are fixed. 

FabricHealer comes with a number of already-implemented/tested target-specific logic rules. You will only need to modify existing rules to get going quickly. FabricHealer is a rule-based repair service and the rules are defined in logic. These rules also form FabricHealer's repair workflow configuration. This is what is meant by Configuration-as-Logic. The only use of XML-based configuration with respect to repair workflow is enabling automitigation (big on/off switch), enabling repair policies, and specifying rule file names. The rest is just the typical Service Fabric application configuration that you know and love. Most of the settings in Settings.xml are overridable parameters and you set the values in ApplicationManifest.xml. This enables versionless parameter-only application upgrades, which means you can change Settings.xml-based settings without redeploying FabricHealer. 

### Repair ephemeral port usage issue for application service process

```Prolog
## Ephemeral Ports - Number of ports in use for any SF service process belonging to the specified SF Application. 
## Attempt the restart code package mitigation for the offending service if the number of ephemeral ports it has opened is greater than 5000.
## Maximum of 5 repairs within a 5 hour window.
Mitigate(AppName="fabric:/IlikePorts", MetricName="EphemeralPorts", MetricValue=?MetricValue) :- ?MetricValue > 5000, 
    TimeScopedRestartCodePackage(5, 05:00:00).
```

### Repair memory usage issue for application service process

```Prolog
## Memory - Percent In Use for any SF service process belonging to the specified SF Application. 
## Attempt the restart code package mitigation for the offending service if the percentage (of total) physical memory it is consuming is at or exceeding 70.
## Maximum of 3 repairs within a 30 minute window.
Mitigate(AppName="fabric:/ILikeMemory", MetricName="MemoryPercent", MetricValue=?MetricValue) :- ?MetricValue >= 70, 
    TimeScopedRestartCodePackage(3, 00:30:00).
```  

## Quickstart


To quickly learn how to use FabricHealer, please see the [simple scenario-based examples.](https://github.com/microsoft/service-fabric-healer/blob/main/Documentation/Using.md) 
