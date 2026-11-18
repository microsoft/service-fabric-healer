param(
    [ValidateSet("win-x64", "win-arm64", "linux-x64")]
    [string[]] $RuntimeIds = @("linux-x64", "win-x64", "win-arm64")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$platforms = @{ "linux-x64" = "Linux"; "win-x64" = "Windows"; "win-arm64" = "WindowsArm64" }
$output = Join-Path $PSScriptRoot 'bin' 'release' 'FabricHealer' 'SFPkgs'
New-Item -ItemType Directory -Force -Path $output | Out-Null

foreach ($rid in $RuntimeIds) {
    foreach ($mode in @("self-contained", "framework-dependent")) {
        $basePath = Join-Path $PSScriptRoot 'bin' 'release' 'FabricHealer' $rid $mode 'FabricHealerType'
        $manifestPath = Join-Path $basePath 'ApplicationManifest.xml'
        if (!(Test-Path -LiteralPath $manifestPath)) { throw "Build $rid before packaging: $basePath" }
        [xml] $manifest = Get-Content -LiteralPath $manifestPath -Raw
        $version = $manifest.ApplicationManifest.ApplicationTypeVersion
        $suffix = if ($mode -eq "self-contained") { "SelfContained" } else { "FrameworkDependent" }
        $target = Join-Path $output "Microsoft.ServiceFabricApps.FabricHealer.$($platforms[$rid]).$suffix.$version.sfpkg"
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
        [IO.Compression.ZipFile]::CreateFromDirectory($basePath, $target)
    }
}
