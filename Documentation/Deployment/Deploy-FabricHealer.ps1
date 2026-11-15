param(
    [Parameter(Mandatory)] [string] $SubscriptionId,
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [string] $TemplateParameterFile = "$PSScriptRoot\service-fabric-healer.v1.4.0.parameters.json"
)

$ErrorActionPreference = "Stop"
Set-AzContext -SubscriptionId $SubscriptionId -ErrorAction Stop | Out-Null
New-AzResourceGroupDeployment -Name "deploy-service-fabric-healer" -ResourceGroupName $ResourceGroup -TemplateFile "$PSScriptRoot\service-fabric-healer.json" -TemplateParameterFile $TemplateParameterFile -Mode Incremental -ErrorAction Stop
