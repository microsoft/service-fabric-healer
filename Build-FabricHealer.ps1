$ErrorActionPreference = "Stop"

$Configuration="Release"
[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

try {
    Push-Location $scriptPath

    Remove-Item $scriptPath\bin\release\FabricHealer\ -Recurse -Force -EA SilentlyContinue

    dotnet publish FabricHealer\FabricHealer.csproj -o bin\release\FabricHealer\linux-x64\self-contained\FabricHealerType\FabricHealerPkg\Code -c $Configuration -r linux-x64 --self-contained true
    dotnet publish FabricHealer\FabricHealer.csproj -o bin\release\FabricHealer\linux-x64\framework-dependent\FabricHealerType\FabricHealerPkg\Code -c $Configuration -r linux-x64 --self-contained false
    dotnet publish FabricHealer\FabricHealer.csproj -o bin\release\FabricHealer\win-x64\self-contained\FabricHealerType\FabricHealerPkg\Code -c $Configuration -r win-x64 --self-contained true
    dotnet publish FabricHealer\FabricHealer.csproj -o bin\release\FabricHealer\win-x64\framework-dependent\FabricHealerType\FabricHealerPkg\Code -c $Configuration -r win-x64 --self-contained false

    Copy-Item FabricHealer\PackageRoot\* bin\release\FabricHealer\linux-x64\self-contained\FabricHealerType\FabricHealerPkg\ -Recurse
    Copy-Item FabricHealer\PackageRoot\* bin\release\FabricHealer\linux-x64\framework-dependent\FabricHealerType\FabricHealerPkg\ -Recurse

    Copy-Item FabricHealer\PackageRoot\* bin\release\FabricHealer\win-x64\self-contained\FabricHealerType\FabricHealerPkg\ -Recurse
    Copy-Item FabricHealer\PackageRoot\* bin\release\FabricHealer\win-x64\framework-dependent\FabricHealerType\FabricHealerPkg\ -Recurse

    Copy-Item FabricHealerApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricHealer\linux-x64\self-contained\FabricHealerType\ApplicationManifest.xml
    Copy-Item FabricHealerApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricHealer\linux-x64\framework-dependent\FabricHealerType\ApplicationManifest.xml
    Copy-Item FabricHealerApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricHealer\win-x64\self-contained\FabricHealerType\ApplicationManifest.xml
    Copy-Item FabricHealerApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricHealer\win-x64\framework-dependent\FabricHealerType\ApplicationManifest.xml
}
finally {
    Pop-Location
}