param(
    [ValidateSet("win-x64", "win-arm64", "linux-x64")]
    [string] $RuntimeId = "win-x64",
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = "$PSScriptRoot\bin\release\FabricHealer\$RuntimeId"

foreach ($mode in @("self-contained", "framework-dependent")) {
    $package = "$root\$mode\FabricHealerType"
    $code = "$package\FabricHealerPkg\Code"
    if (Test-Path -LiteralPath $package) {
        Remove-Item -LiteralPath $package -Recurse -Force
    }

    dotnet publish "$PSScriptRoot\FabricHealer\FabricHealer.csproj" -f net10.0 -c $Configuration -r $RuntimeId --self-contained ($mode -eq "self-contained").ToString().ToLowerInvariant() -o $code
    if ($LASTEXITCODE -ne 0) { throw "FabricHealer $RuntimeId $mode publish failed: $LASTEXITCODE" }

    Copy-Item "$PSScriptRoot\FabricHealer\PackageRoot\*" "$package\FabricHealerPkg" -Recurse -Force
    Copy-Item "$PSScriptRoot\FabricHealerApp\ApplicationPackageRoot\ApplicationManifest.xml" "$package\ApplicationManifest.xml"
    $manifestPath = "$package\FabricHealerPkg\ServiceManifest.xml"
    [xml] $manifest = Get-Content -LiteralPath $manifestPath -Raw
    $manifest.ServiceManifest.CodePackage.EntryPoint.ExeHost.Program = if ($RuntimeId -like "win-*") { "FabricHealer.exe" } else { "FabricHealer" }
    $manifest.Save($manifestPath)
}
