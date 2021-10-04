# FabricHealer Preview
### Configuration as Logic and auto-mitigation in Service Fabric clusters

FabricHealer is a Service Fabric application that attempts to automatically fix a set of reliably solvable problems that can take place in Service Fabric applications
(including containers), host virtual machines, and logical disks (scoped to space usage problems only).
These repairs mostly employ a set of Service Fabric API calls, but can also be fully customizable (like Disk repair).
All repairs are safely orchestrated through Service Fabric’s RepairManager service.
Repair workflow configuration is written as [Prolog](http://www.let.rug.nl/bos/lpn//lpnpage.php?pageid=online)-like [logic](/FabricHealer/PackageRoot/Config/LogicRules) with supporting external predicates written in C#.
FabricHealer's Configuration-as-Logic feature is made possible by a new logic programming library for .NET, [Guan](https://github.com/microsoft/guan), also in Preview.
The fun starts when FabricHealer detects supported error or warning health events reported by [FabricObserver](https://github.com/microsoft/service-fabric-observer).

FabricHealer is implemented as a stateless singleton service that runs on all nodes in a Linux or Windows Service Fabric cluster. It is a .NET Core 3.1 application and has been tested on Windows (2016/2019) and Ubuntu (16/18.04).  

All warning and error health reports created by [FabricObserver](https://github.com/microsoft/service-fabric-observer) and subsequently repaired by FabricHealer are user-configured - developer control extends from unhealthy event source to related healing operations.
FabricObserver and FabricHealer are part of a family of highly configurable Service Fabric observability tools that work together to keep your clusters green.

To learn more about FabricHealer's configuration-as-logic model, [click here.](Documentation/LogicWorkflows.md)  

```
FabricHealer requires that FabricObserver (v 3.1.8+) and RepairManager (RM) service are deployed. 
```
```
For VM level repair, InfrastructureService (IS) service must be deployed.
```
```
This is Preview technology and is not meant for production use. Only use in Test environments.
```
We are very interested in your feedback with both repair reliability and the Configuration-as-Logic feature. Please let us know what you think. Simply create Issues with your feedback and any bugs run into/enhancements you think are necessary. Thank you.  

Also, a reminder that this is preview quality software and there are probably some minor bugs and the code will definitely churn, but it is stable and solid.
It is capable as is today and appropriate for use in **test** enviroments. It has been tested in both Linux and Windows deployments.
The current set of repair workflows work and should perform correctly in your clusters. Please create Issues on this repo if you find bugs. If you are comfortable fixing them, then
pull requests will be evaluated and merged if they meet the quality bar. Thanks in advance for your partnership and for experimenting with FabricHealer.

## Build and run  

1. Clone the repo.
2. Install [.NET Core 3.1](https://dotnet.microsoft.com/download/dotnet-core/3.1)
3. Build. 

***Note: FabricHealer must be run under the LocalSystem account (see ApplicationManifest.xml) in order to function correctly. This means on Windows, by default, it will run as System user. On Linux, by default, it will run as root user. You do not have to make any changes to ApplicationManifest.xml for this to be the case.*** 

## Using FabricHealer  

```FabricHealer is a service specifically designed to auto-mitigate Service Fabric service issues that are generally the result of bugs in user code.```  

Let's say you have a service that leaks memory or ephemeral ports. You would use FabricHealer to keep the problem in check while your developers figure out the root cause and fix the bug(s) that lead to resource usage over-consumption. FabricHealer is really just a temporary solution to problems, not a fix. This is how you should think about auto-mitigation, generally. FabricHealer aims
to keep your cluster green while you fix your bugs. With it's configuration-as-logic support, you can easily specify that some repair for some service should only be attempted for n weeks or months, while your dev team fixes the underlying issues with the problematic service. FabricHealer should be thought of as a "disappearing task force" in that it can provide stability during times of instability, then "go away" when bugs are fixed. 

FabricHealer comes with a number of already-implemented/tested target-specific logic rules. You will only need to modify existing rules to get going quickly. FabricHealer is a rule-based repair service and the rules are defined in logic. These rules also form FabricHealer's repair workflow configuration. This is what is meant by Configuration-as-Logic. The only use of XML-based configuration with respect to repair workflow is enabling automitigation (big on/off switch), enabling repair policies, and specifying rule file names. The rest is just the typical Service Fabric application configuration that you know and love. Most of the settings in
Settings.xml are overridable parameters and you set the values in ApplicationManifest.xml. This enables versionless parameter-only application upgrades, which means you can change Settings.xml-based settings without redeploying FabricHealer.

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
