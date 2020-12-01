function Build-Nuget {
    param (
        [string]
        $packageId,

        [string]
        $basePath
    )

    [string] $nugetSpecTemplate = [System.IO.File]::ReadAllText([System.IO.Path]::Combine($scriptPath, "FabricHealer.nuspec.template"))

    [string] $nugetSpecPath = "$scriptPath\bin\release\FabricHealer\$($packageId).nuspec"

    [System.IO.File]::WriteAllText($nugetSpecPath,  $nugetSpecTemplate.Replace("%PACKAGE_ID%", $packageId))

    .\nuget.exe pack $nugetSpecPath -basepath $basePath -OutputDirectory bin\release\FabricHealer\Nugets -properties NoWarn=NU5100
}

[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

try {
    Push-Location $scriptPath

    Build-Nuget "FabricHealer.Linux.SelfContained" "$scriptPath\bin\release\FabricHealer\linux-x64\self-contained\FabricHealerType"
    Build-Nuget "FabricHealer.Linux.FrameworkDependent" "$scriptPath\bin\release\FabricHealer\linux-x64\framework-dependent\FabricHealerType"

    Build-Nuget "FabricHealer.Windows.SelfContained" "$scriptPath\bin\release\FabricHealer\win-x64\self-contained\FabricHealerType"
    Build-Nuget "FabricHealer.Windows.FrameworkDependent" "$scriptPath\bin\release\FabricHealer\win-x64\framework-dependent\FabricHealerType"
}
finally {
    Pop-Location
}