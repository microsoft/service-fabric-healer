param([string] $Configuration = "Release")

$ErrorActionPreference = "Stop"
dotnet pack "$PSScriptRoot\FabricHealerProxy\FabricHealerProxy.csproj" -c $Configuration -o "$PSScriptRoot\bin\release\FabricHealerProxy\Nugets"
if ($LASTEXITCODE -ne 0) { throw "FabricHealerProxy pack failed: $LASTEXITCODE" }
