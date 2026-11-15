# Deploy FabricHealer using ARM

FabricHealer 1.4.0 uses .NET 10 and the SF runtime 11.8.121.1 / SDK 8.8.121
pairing. Framework-dependent deployments require the .NET 10 runtime on every
target node; self-contained deployments include it. Build with the SDK pinned
in `global.json`, then use `Build-SFPKGs.ps1` to package the selected RIDs and
both deployment modes. The generated entry point is `FabricHealer.exe` on
Windows and `FabricHealer` on Linux.

The .NET 10 Linux SF runtime is not yet supported; Linux x64 is build-only.
Cross-building Windows ARM64 does not establish ARM64 runtime validation.
Do not treat one-machine development-cluster results as upgrade validation.
Use a supported multi-node cluster for monitored upgrades and rollback tests.

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