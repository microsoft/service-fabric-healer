$ErrorActionPreference = "Stop"

$Configuration="Release"
[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

try {
    Push-Location $scriptPath

    Remove-Item $scriptPath\FabricHealerLib\bin\release\netstandard2.0\ -Recurse -Force -EA SilentlyContinue

    dotnet publish $scriptPath\FabricHealerLib\FabricHealerLib.csproj -o bin\release\netstandard2.0 -c $Configuration
}
finally {
    Pop-Location
}