# Deploy FabricHealer using ARM

## Requirements

FabricHealer 1.4.0 uses .NET 10. Its selected and validated Service Fabric pairing
is runtime **11.8.121.1** with SDK **8.8.121**. Use this pairing or a supported,
compatible Service Fabric release.

| Platform | Validation for this release |
|---|---|
| Windows x64 | Runtime-tested in Azure in both deployment modes |
| Windows ARM64 | Build/package only; not runtime-tested |
| Linux x64 | Build/package only; .NET 10 Service Fabric runtime support is not yet available |

### .NET runtime

- **Self-contained:** The package includes its .NET runtime.
- **Framework-dependent:** Install at least the .NET 10 patch recorded in
  `FabricHealerPkg/Code/FabricHealer.runtimeconfig.json` under
  `runtimeOptions.framework.version` on every target node. An older .NET 10
  patch cannot start that package; a newer compatible patch can.

The projects enable `TargetLatestRuntimePatch`, so rebuilding with a newer SDK
can raise the minimum patch in the generated runtime configuration. Check the
file in the package being deployed rather than assuming any .NET 10 installation
is sufficient.

RepairManager must be enabled. Machine repairs also require InfrastructureService
for the target node type. Use a supported multi-node cluster for monitored
upgrades and rollback tests, not a one-machine development cluster.

## Deploy

Choose the package for the target platform and deployment mode. Build/package
instructions are in the [development guide](../Development.md#build-and-package).

Supply `-SubscriptionId`, `-ResourceGroup`, and optionally `-TemplateParameterFile`
to `Deploy-FabricHealer.ps1`. Keep private parameter files, credentials, and
temporary package URLs outside the repository. No script publishes packages.

Like all elementary services such as resource monitoring, Windows Update, scripts, scaling, etc., FabricHealer should be installed with your initial Service Fabric cluster deployment. 

There are two options:
1. Add the resource provided in the ARM template [service-fabric-healer.json](service-fabric-healer.json) in the template which also deploys the Service Fabric cluster.
   To guarantee the correct deployment order the first resource has to depend on the cluster resource.
   Using 'dependsOn' makes sure that the Service Fabric Resource Provider deploys the application only after the cluster deployment step has completed.

```ARM
    {
      "apiVersion": "[variables('sfApiVersion')]",
      "type": "Microsoft.ServiceFabric/clusters/applicationTypes",
      "name": "[concat(parameters('clusterName'), '/', variables('applicationTypeName'))]",
      "location": "[resourceGroup().location]",
      "dependsOn": [
        "[concat('Microsoft.ServiceFabric/clusters/', parameters('clusterName'))]"
      ],
      "properties": {
        "provisioningState": "Default"
      }
    },
``` 

2. The app can be deployed manually by using the provided PowerShell script file [Deploy-FabricHealer.ps1](Deploy-FabricHealer.ps1).


## Further reading
- [Upgrade the Service Fabric application by using Resource Manager](https://docs.microsoft.com/en-us/azure/service-fabric/service-fabric-concept-resource-model#upgrade-the-service-fabric-application-by-using-resource-manager)