$ErrorActionPreference = "Stop"

$Configuration="Release"
[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

try {
    Push-Location $scriptPath

    Remove-Item $scriptPath\FabricHealerProxy\bin\release\net6.0\ -Recurse -Force -EA SilentlyContinue

    dotnet publish $scriptPath\FabricHealerProxy\FabricHealerProxy.csproj -o bin\release\net6.0 -c $Configuration
}
finally {
    Pop-Location
}