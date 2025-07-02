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

try {
    Push-Location $scriptPath

    Remove-Item $scriptPath\FabricHealerProxy\bin\release\net8.0\$RuntimeId -Recurse -Force -EA SilentlyContinue
    dotnet publish $scriptPath\FabricHealerProxy\FabricHealerProxy.csproj $winArmSFPackageRefOverride -o bin\release\net8.0 -c $Configuration
}
finally 
{
    Pop-Location
}