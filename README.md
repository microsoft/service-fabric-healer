## FabricHealer 1.2.1
### Service Fabric Auto-Repair Service with Declarative Logic for Repair Workflow Configuration

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fmicrosoft%2Fservice-fabric-healer%2Fmain%2FDocumentation%2FDeployment%2Fservice-fabric-healer.json)

FabricHealer (FH) is a .NET 6 Service Fabric application that attempts to automatically fix a set of reliably solvable problems that can take place in Service Fabric
applications (including containers), host virtual machines, and logical disks (scoped to space usage problems only). These repairs mostly employ a set of Service Fabric API calls, but can also be fully customizable (like Disk repair). All repairs are safely orchestrated through the Service Fabric RepairManager system service.
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
FabricHealer requires the Service Fabric RepairManager (RM) service. 
```
```
For machine repairs, Service Fabric InfrastructureService (IS) must be deployed for each node type.
```

## Build and run  

1. Clone the repo.
2. Install [.NET 6](https://dotnet.microsoft.com/download/dotnet-core/6.0)
3. Build. 

## Deploy FabricHealer 
You can deploy FabricHealer using Visual Studio (if you build the sources yourself), PowerShell or ARM. ***Please note*** that this version of FabricHealer no longer supports the DefaultServices node in ApplicationManifest.xml. This means that should you deploy using PowerShell, you must create an instance of the service as the last command in your script. This was done to support ARM deployment, specifically.
The StartupServices.xml file you see in the FabricHealerApp project now contains the service information once held in ApplicationManifest's DefaultServices node. Note that this information is primarily useful for deploying from Visual Studio. Your ARM template or PowerShell script will contain all the information necessary for deploying FabricHealer.

### ARM Deployment

For ARM deployment, please see the [ARM documentation](/Documentation/Deployment/Deployment.md). 

### PowerShell Deployment

```PowerShell

#cd to the top level repo directory where you cloned FO sources.

cd C:\Users\me\source\repos\service-fabric-healer

#Build FH (Release)

./Build-FabricHealer

#create a $path variable that points to the build output:
#E.g., for Windows deployments:

$path = "C:\Users\me\source\repos\service-fabric-healer\bin\release\FabricHealer\win-x64\self-contained\FabricHealerType"

#For Linux deployments:

#$path = "C:\Users\me\source\repos\service-fabric-healer\bin\release\FabricHealer\linux-x64\self-contained\FabricHealerType"

#Connect to target cluster, for example:

Connect-ServiceFabricCluster -ConnectionEndpoint @('sf-win-cluster.westus2.cloudapp.azure.com:19000') -X509Credential -FindType FindByThumbprint -FindValue '[thumbprint]' -StoreLocation LocalMachine -StoreName 'My'

#Copy $path contents (FO app package) to server:

Copy-ServiceFabricApplicationPackage -ApplicationPackagePath $path -CompressPackage -ApplicationPackagePathInImageStore FH1110 -TimeoutSec 1800

#Register FO ApplicationType:

Register-ServiceFabricApplicationType -ApplicationPathInImageStore FH121

#Create FO application (if not already deployed at lesser version):

New-ServiceFabricApplication -ApplicationName fabric:/FabricHealer -ApplicationTypeName FabricHealerType -ApplicationTypeVersion 1.2.1   

#Create the Service instance:  

# FH can be deployed with a single instance or run on all nodes. It's up to you. Note that for certain repairs, it must run as -1 (Disk repairs (file deletion), System service process restarts).
New-ServiceFabricService -Stateless -PartitionSchemeSingleton -ApplicationName fabric:/FabricHealer -ServiceName fabric:/FabricHealer/FabricHealerService -ServiceTypeName FabricHealerType -InstanceCount -1

#OR if updating existing version:  

Start-ServiceFabricApplicationUpgrade -ApplicationName fabric:/FabricHealer -ApplicationTypeVersion 1.2.1 -Monitored -FailureAction rollback
```  

## Using FabricHealer  

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

## Operational Telemetry 
Please see [FabricHealer Operational Telemetry](/Documentation/OperationalTelemetry.md) for detailed information on the user agnostic (Non-PII) data FabricHealer sends to Microsoft (opt out with a simple configuration parameter change).
Please consider leaving this enabled so your friendly neighborhood Service Fabric devs can understand how FabricHealer is doing in the real world. We would really appreciate it!


## Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit https://cla.opensource.microsoft.com.

When you submit a pull request, a CLA bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.
