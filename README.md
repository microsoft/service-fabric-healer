# FabricHealer Beta
## Auto-mitigation in Service Fabric clusters

FabricHealer (FH) is a singleton stateless Service Fabric service that runs on all nodes in a Linux or Windows cluster. It is implemented as a .NET Core 3.1 application and has been tested on Windows (2016/2019) and Ubuntu (18.04).
Its primary purpose is to schedule and execute automatic repairs in Service Fabric clusters after evaluating health reports created by [FabricObserver](https://github.com/microsoft/service-fabric-observer) (FO) instances running in the same cluster. FabricHealer will run a set of mitigation operations that are part of the Service Fabric SDK as well as custom implementations (currently disk file deletions would classify as custom), of which all reoairs are orchestrated through Service Fabric’s RepairManager service. As such, your cluster must have the RepairManager service deployed. 

All warnings and error reports created by FabricObserver instances are user-configured. Developer control extends from unhealthy event source (FO) to related healing operations (FH). This is a key part of the design.  

```
This is a beta quality version (in both design and implementation) 
and is not meant nor supported for use production - that will come with learnings from the beta. 
```

## Configuration as Logic
FabricHealer leverages the power of logic programming with Prolog-like semantics/syntax to express repair workflows in configuration. To learn more [click here.](Documentation/LogicWorkflows.md)

## Quickstart

For some examples and to quickly learn how to use FH, please see the [simple scenario-based examples.](Documentation/Using.md)

## High Level FabricHealer Workflow  

![alt text](FHDT.png "") 
