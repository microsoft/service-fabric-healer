function Install-Nuget {
    # Path to Latest nuget.exe on nuget.org
    $source = "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe"

    # Save file to top level directory in repo
    $destination = "$scriptPath\nuget.exe"

    #Download the file
    if (-Not [System.IO.File]::Exists($destination)) {
        Invoke-WebRequest -Uri $source -OutFile $destination
    }
}

function Build-Nuget {
    param (
        [string]
        $packageId,

        [string]
        $basePath
    )

    [string] $nugetSpecTemplate = [System.IO.File]::ReadAllText([System.IO.Path]::Combine($scriptPath, "FabricHealer.nuspec.template"))

    [string] $nugetSpecPath = "$scriptPath\bin\release\FabricHealer\$($packageId).nuspec"

    [System.IO.File]::WriteAllText($nugetSpecPath,  $nugetSpecTemplate.Replace("%PACKAGE_ID%", $packageId).Replace("%ROOT_PATH%", $scriptPath))

    .\nuget.exe pack $nugetSpecPath -basepath $basePath -OutputDirectory bin\release\FabricHealer\Nugets -properties NoWarn=NU5100,NU5128
}

[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

try {

    Push-Location $scriptPath

    Install-Nuget

    Build-Nuget "Microsoft.ServiceFabricApps.FabricHealer.Linux.SelfContained" "$scriptPath\bin\release\FabricHealer\linux-x64\self-contained\FabricHealerType"
    Build-Nuget "Microsoft.ServiceFabricApps.FabricHealer.Linux.FrameworkDependent" "$scriptPath\bin\release\FabricHealer\linux-x64\framework-dependent\FabricHealerType"

    Build-Nuget "Microsoft.ServiceFabricApps.FabricHealer.Windows.SelfContained" "$scriptPath\bin\release\FabricHealer\win-x64\self-contained\FabricHealerType"
    Build-Nuget "Microsoft.ServiceFabricApps.FabricHealer.Windows.FrameworkDependent" "$scriptPath\bin\release\FabricHealer\win-x64\framework-dependent\FabricHealerType"
}
finally {
    Pop-Location
}