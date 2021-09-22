# FabricHealer Preview
### Configuration as Logic and auto-mitigation in Service Fabric clusters

FabricHealer is a Service Fabric application that attempts to fix a set of reliably solvable problems that can take place in a Service Fabric application service or host virtual machine, including logical disks, but scoped to space usage problems only. These repairs mostly employ a set of Service Fabric API calls, but can also be fully custom. All repairs are orchestrated through Service Fabric’s RepairManager service. Repair configuration is written as [Prolog](http://www.learnprolognow.org/)-like [logic](https://github.com/microsoft/service-fabric-healer/tree/main/FabricHealer/PackageRoot/Config/Rules) with [supporting external predicates](https://github.com/microsoft/service-fabric-healer/tree/main/FabricHealer/Repair/Guan) written in C#. This is made possible by a new logic programming system, [Guan](https://github.com/microsoft/guan). The fun starts when FabricHealer detects supported error or warning states reported by [FabricObserver](https://github.com/microsoft/service-fabric-observer) running in the same cluster.  

FabricHealer is implemented as a stateless singleton service that runs on all nodes in a Linux or Windows Service Fabric cluster. It is a .NET Core 3.1 application and has been tested on Windows (2016/2019) and Ubuntu (16/18.04).  

All warning and error reports created by [FabricObserver](https://github.com/microsoft/service-fabric-observer) and subsequently repaired by FabricHealer are user-configured - developer control extends from unhealthy event source to related healing operations.

To learn more about FabricHealer's configuration-as-logic model, [click here.](Documentation/LogicWorkflows.md)

```
FabricHealer requires that FabricObserver (v 3.1.8+) and RepairManager (RM) service are deployed. 
```
```
For VM level repair, InfrastructureService (IS) service must be deployed.
```

## For Early Adopters while in Preview

Please [download the Guan nupkg](https://github.com/microsoft/Guan/releases/download/1.0.0-Preview/Microsoft.Logic.Guan.1.0.0-Preview.nupkg) to your local dev machine and install it into your local FH project in order to build FH successfully. This will be unnecessary when FH ships in Public Preview as Guan will be shipping concurrently and the Guan nupkg will be available in the nuget.org package gallery, as will FH.  

```
This is Preview technology and is not meant for production use. Only use in Test environments.
```
In this release, we are experimenting with a new approach to health validation for repaired targets. FabricHealer will assume success when some repair operation is successful (like restarting a code package or system service process). FH
will emit an Ok Health Report that will clear FabricObserver's Warning on the target. This is because FH has no understanding of the source Observer's run intervals. If in fact, the repair did not solve the problem, then FO will emit another warning
for the health entity with specific details of the problem and FH will treat this as a new repair target. This has been implemented for service healing and system process healing in this release. For node and VM level repairs, FH will wait until FO reports health state
for the target node before deciding a repair was successful or not.

Also, a reminder that this is beta quality software and there are probably some bugs and the code will churn. That said, it is usable as is today and appropriate for use in **test** enviroments. Please create Issues on this repo if you find bugs. If you are comfortable fixing them, then
pull requests will be evaluated and merged if they meet the quality bar. Thanks in advance for your partnership and for experimenting with FabricHealer.
## Quickstart

To quickly learn how to use FabricHealer, please see the [simple scenario-based examples.](Documentation/Using.md)

# Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit https://cla.opensource.microsoft.com.

When you submit a pull request, a CLA bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.
