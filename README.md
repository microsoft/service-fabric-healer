# FabricHealer Beta
### Configuration as logic and auto-mitigation in Service Fabric clusters

FabricHealer is a Service Fabric application that attempts to fix a set of reliably solvable problems that can take place in a Service Fabric application service or host virtual machine, including logical disks, but scoped to space usage problems only. These repairs mostly employ a set of Service Fabric API calls, but can also be fully custom. All repairs are orchestrated through Service Fabric’s RepairManager service. Repair configuration is written as [Prolog](http://www.learnprolognow.org/)-like [logic](https://github.com/microsoft/service-fabric-healer/tree/main/FabricHealer/PackageRoot/Config/Rules) with [supporting external predicates](https://github.com/microsoft/service-fabric-healer/tree/main/FabricHealer/Repair/Guan) written in C#. This is made possible by a new logic programming system, [Guan](https://github.com/microsoft/guan). The fun starts when FabricHealer detects supported error or warning states reported by [FabricObserver](https://github.com/microsoft/service-fabric-observer) running in the same cluster.  

To learn more about FabricHealer's configuration-as-logic model, [click here.](Documentation/LogicWorkflows.md)

```
FabricHealer requires that FabricObserver is deployed in the same cluster. 
```
FabricHealer is implemented as a stateless singleton service that runs on all nodes 
in a Linux or Windows Service Fabric cluster. It is a .NET Core 3.1 application and has been tested on 
Windows (2016/2019) and Ubuntu (16/18.04).  

All warning and error reports created by [FabricObserver](https://github.com/microsoft/service-fabric-observer) and subsequently repaired by FabricHealer are user-configured - developer control extends from unhealthy event source to related healing operations.

```
This is a pre-release and is not meant for use in production. 
```
## Quickstart

To quickly learn how to use FabricHealer, please see the [simple scenario-based examples.](Documentation/Using.md)


## For Earl Adopters while in Private Preview

Please [download the Guan nupkg](https://github.com/microsoft/service-fabric-healer/releases/download/39178748/Microsoft.ServiceFabricApps.Guan.1.0.0.nupkg) from the Releases section of this repo to your local dev machine and install it into your local FH project in order to build FH successfully. This will be unnecessary when FH ships in Public Preview as Guan will be shipping concurrently and the Guan nupkg will be available in the nuget.org package gallery, as will FH.
