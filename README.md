# FabricHealer  
## Auto-mitigation in Service Fabric clusters

FabricHealer (FH) is a singleton stateless Service Fabric service that runs on all nodes in a Linux or Windows cluster. It is implemented as a .NET Core 3.1 application and has been tested on Windows (2016/2019) and Ubuntu (18.04).
Its primary purpose is to schedule and execute automatic repairs in Service Fabric clusters after inspecting unhealthy events created by [FabricObserver](https://github.com/microsoft/service-fabric-observer) (FO) instances running in the same cluster. Like FO, FH only does what it is configured to do by an end user and does not attempt to conduct its own determination for running a supported auto-mitigation operation, of which all are orchestrated through Service Fabric’s RepairManager service. All warnings and errors signaled by FabricObserver instances are user-configured, so user control extends from unhealthy event source (FO) to related healing operation/workflow (FH). This is a key part of the design.  

## Configuration as Logic
FabricHealer leverages the power of logic programming with Prolog-like semantics/syntax to express repair workflows in configuration. To learn more [click here.](Documentation/LogicWorkflows.md)

## Quickstart

For some examples and to quickly learn how to use FH, please see the [simple scenario-based examples.](Documentation/Using.md)

## High Level FabricHealer Workflow  

![alt text](FHDT.png "") 
