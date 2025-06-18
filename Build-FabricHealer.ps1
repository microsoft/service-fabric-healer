param(
	[string] $RuntimeId = "win-x64",
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"

[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition
[string] $winArmSFPackageRefOverride = "/p:VersionOverride_SFServices=7.0.1816"

# For SF 11/12 arm64 builds, today we need to override the SF package reference version to match the current version of the SDK 
# to ensure ARM64 x64 emulation works correctly.
if ($RuntimeId -eq "win-arm64") 
{
    $winArmSFPackageRefOverride = "/p:IsArmTarget=true"
}

try 
{
    Push-Location $scriptPath

    Remove-Item $scriptPath\bin\release\FabricHealer\ -Recurse -Force -EA SilentlyContinue

    dotnet publish FabricHealer\FabricHealer.csproj $winArmSFPackageRefOverride -o bin\release\FabricHealer\$RuntimeId\self-contained\FabricHealerType\FabricHealerPkg\Code -c $Configuration -r $RuntimeId --self-contained true
    dotnet publish FabricHealer\FabricHealer.csproj $winArmSFPackageRefOverride -o bin\release\FabricHealer\$RuntimeId\framework-dependent\FabricHealerType\FabricHealerPkg\Code -c $Configuration -r $RuntimeId --self-contained false
    
    Copy-Item FabricHealer\PackageRoot\* bin\release\FabricHealer\$RuntimeId\self-contained\FabricHealerType\FabricHealerPkg\ -Recurse
    Copy-Item FabricHealer\PackageRoot\* bin\release\FabricHealer\$RuntimeId\framework-dependent\FabricHealerType\FabricHealerPkg\ -Recurse

    Copy-Item FabricHealerApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricHealer\$RuntimeId\self-contained\FabricHealerType\ApplicationManifest.xml
    Copy-Item FabricHealerApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricHealer\$RuntimeId\framework-dependent\FabricHealerType\ApplicationManifest.xml    
}
finally 
{
    Pop-Location
}