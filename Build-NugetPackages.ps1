param(
    [string] $Configuration = "Release",
    [ValidateSet("linux-x64", "win-x64", "win-arm64")]
    [string[]] $RuntimeIds = @("linux-x64", "win-x64", "win-arm64")
)

$ErrorActionPreference = "Stop"
$platforms = [ordered]@{ "linux-x64" = "Linux"; "win-x64" = "Windows"; "win-arm64" = "WindowsArm64" }
foreach ($rid in $RuntimeIds) {
    foreach ($mode in @("self-contained", "framework-dependent")) {
        $basePath = Join-Path $PSScriptRoot 'bin' 'release' 'FabricHealer' $rid $mode 'FabricHealerType'
        if (!(Test-Path (Join-Path $basePath 'ApplicationManifest.xml'))) { throw "Build $rid before packaging: $basePath" }
        $suffix = if ($mode -eq "self-contained") { "SelfContained" } else { "FrameworkDependent" }
        $id = "Microsoft.ServiceFabricApps.FabricHealer.$($platforms[$rid]).$suffix"

        $project = Join-Path $PSScriptRoot 'FabricHealer' 'FabricHealer.csproj'
        $output = Join-Path $PSScriptRoot 'bin' 'release' 'FabricHealer' 'Nugets'
        dotnet pack $project -c $Configuration "-p:DeploymentPackageId=$id" "-p:DeploymentPackagePath=$basePath" -o $output
        if ($LASTEXITCODE -ne 0) { throw "$id pack failed: $LASTEXITCODE" }
    }
}
