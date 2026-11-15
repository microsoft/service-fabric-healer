param(
    [string] $Configuration = "Release",
    [ValidateSet("linux-x64", "win-x64", "win-arm64")]
    [string[]] $RuntimeIds = @("linux-x64", "win-x64", "win-arm64")
)

$ErrorActionPreference = "Stop"
$platforms = [ordered]@{ "linux-x64" = "Linux"; "win-x64" = "Windows"; "win-arm64" = "WindowsArm64" }
foreach ($rid in $RuntimeIds) {
    foreach ($mode in @("self-contained", "framework-dependent")) {
        $basePath = "$PSScriptRoot\bin\release\FabricHealer\$rid\$mode\FabricHealerType"
        if (!(Test-Path "$basePath\ApplicationManifest.xml")) { throw "Build $rid before packaging: $basePath" }
        $suffix = if ($mode -eq "self-contained") { "SelfContained" } else { "FrameworkDependent" }
        $id = "Microsoft.ServiceFabricApps.FabricHealer.$($platforms[$rid]).$suffix"

        dotnet pack "$PSScriptRoot\FabricHealer\FabricHealer.csproj" -c $Configuration "-p:DeploymentPackageId=$id" "-p:DeploymentPackagePath=$basePath" -o "$PSScriptRoot\bin\release\FabricHealer\Nugets"
        if ($LASTEXITCODE -ne 0) { throw "$id pack failed: $LASTEXITCODE" }
    }
}
