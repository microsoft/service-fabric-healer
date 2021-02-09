[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

function Build-SFPkg {
    param (
        [string]
        $packageId,

        [string]
        $basePath
    )

    $ProgressPreference = "SilentlyContinue"

    [string] $outputDir = "$scriptPath\bin\release\FabricHealer\SFPkgs"
    [string] $zipPath = "$outputDir\$($packageId).zip"
    [System.IO.Directory]::CreateDirectory($outputDir) | Out-Null

    Compress-Archive "$basePath\*"  $zipPath -Force

    Move-Item -Path $zipPath -Destination ($zipPath.Replace(".zip", ".sfpkg"))
}

try {
    Push-Location $scriptPath

    Build-SFPkg "Microsoft.ServiceFabricApps.FabricHealer.Linux.SelfContained.Beta.0.4.2" "$scriptPath\bin\release\FabricHealer\linux-x64\self-contained\FabricHealerType"
    Build-SFPkg "Microsoft.ServiceFabricApps.FabricHealer.Linux.FrameworkDependent.Beta.0.4.2" "$scriptPath\bin\release\FabricHealer\linux-x64\framework-dependent\FabricHealerType"

    Build-SFPkg "Microsoft.ServiceFabricApps.FabricHealer.Windows.SelfContained.Beta.0.4.2" "$scriptPath\bin\release\FabricHealer\win-x64\self-contained\FabricHealerType"
    Build-SFPkg "Microsoft.ServiceFabricApps.FabricHealer.Windows.FrameworkDependent.Beta.0.4.2" "$scriptPath\bin\release\FabricHealer\win-x64\framework-dependent\FabricHealerType"
}
finally {
    Pop-Location
}
