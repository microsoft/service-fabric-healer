
$subscriptionId = "<YOUR-AZURE-SUBSCRIPTION-ID>" 
Try {
  Select-AzSubscription -SubscriptionId $subscriptionId -ErrorAction Stop
} Catch {
    Login-AzAccount
    Set-AzContext -SubscriptionId $subscriptionId
}

$resourceGroup = "<YOUR-CLUSTER-RESOURCE-NAME>"
$armTemplate = "service-fabric-healer.json"
$armTemplateParameters = "service-fabric-healer.v2.0.0.831.parameters.json"

cd "<LOCAL-FH-REPO-PATH>\Documentation\Deployment"

New-AzResourceGroupDeployment -Name "deploy-service-fabric-healer" -ResourceGroupName $resourceGroup -TemplateFile $armTemplate -TemplateParameterFile $armTemplateParameters -Verbose -Mode Incremental

