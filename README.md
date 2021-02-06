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

All warning and error reports created by [FabricObserver](https://github.com/microsoft/service-fabric-observer) and subsequently repaired by FabricHealer are user-configured - developer control extends from unhealthy event source (FO) to related healing operations (FH).

```
This is a pre-release and is not meant for use in production. 
```
## Quickstart

To quickly learn how to use FH, please see the [simple scenario-based examples.](Documentation/Using.md)
