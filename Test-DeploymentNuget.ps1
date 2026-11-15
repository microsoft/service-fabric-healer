param(
    [ValidateSet("linux-x64", "win-x64", "win-arm64")]
    [string[]] $RuntimeIds = @("linux-x64", "win-x64", "win-arm64"),
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$platforms = @{ "linux-x64" = "Linux"; "win-x64" = "Windows"; "win-arm64" = "WindowsArm64" }
$nugets = Join-Path $PSScriptRoot 'bin' 'release' 'FabricHealer' 'Nugets'
$consumers = Join-Path $PSScriptRoot 'bin' 'release' 'FabricHealer' 'ConsumerTests'
$assetsPath = Join-Path $PSScriptRoot 'FabricHealer' 'obj' 'project.assets.json'
if (!(Test-Path -LiteralPath $assetsPath)) { throw "Build and pack FabricHealer before validating deployment NuGets." }
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$sources = @("--source", $nugets)
foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
    $sources += @("--source", $folder)
}
[xml] $hostProject = Get-Content (Join-Path $PSScriptRoot 'FabricHealer' 'FabricHealer.csproj') -Raw
$version = $hostProject.Project.PropertyGroup.Version
if (!$version) { throw "FabricHealer package version is missing." }
if (Test-Path -LiteralPath $consumers) { Remove-Item -LiteralPath $consumers -Recurse -Force }
New-Item -ItemType Directory -Force -Path $consumers | Out-Null
$cache = Join-Path $consumers 'packages'

foreach ($rid in $RuntimeIds) {
    foreach ($suffix in @("SelfContained", "FrameworkDependent")) {
        $id = "Microsoft.ServiceFabricApps.FabricHealer.$($platforms[$rid]).$suffix"
        $packagePath = Join-Path $nugets "$id.$version.nupkg"
        if (!(Test-Path -LiteralPath $packagePath)) { throw "Deployment package is missing: $packagePath" }
        $zip = [IO.Compression.ZipFile]::OpenRead($packagePath)
        try {
            $specEntry = @($zip.Entries | Where-Object { $_.FullName.EndsWith(".nuspec") })
            if ($specEntry.Count -ne 1) { throw "$id must contain exactly one NuGet specification." }
            $reader = [IO.StreamReader]::new($specEntry[0].Open())
            try { [xml] $spec = $reader.ReadToEnd() }
            finally { $reader.Dispose() }
            $rules = @($spec.package.metadata.contentFiles.files)
            if ($rules.Count -eq 0 -or @($rules | Where-Object { $_.copyToOutput -ne "true" }).Count) {
                throw "$id must copy every deployment content file to consumer output."
            }
            if (@($rules | Where-Object { $_.flatten -eq "true" }).Count) {
                throw "$id must preserve the deployment directory structure."
            }
            $contentRoot = "contentFiles/any/any/"
            $content = @($zip.Entries | Where-Object { $_.FullName.StartsWith($contentRoot) -and $_.Name })
            $program = if ($rid -like "win-*") { "FabricHealer.exe" } else { "FabricHealer" }
            if (!$zip.GetEntry("${contentRoot}FabricHealerPkg/Code/$program")) {
                throw "$id is missing the executable at the Service Fabric manifest path."
            }
            $consumer = Join-Path $consumers $rid $suffix
            New-Item -ItemType Directory -Force -Path $consumer | Out-Null
            $project = Join-Path $consumer 'Consumer.csproj'
            $projectXml = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="$id" Version="[$version]" /></ItemGroup>
</Project>
"@
            [IO.File]::WriteAllText($project, $projectXml)
            dotnet restore $project --packages $cache @sources
            if ($LASTEXITCODE -ne 0) { throw "$id consumer restore failed: $LASTEXITCODE" }
            dotnet build $project -c $Configuration --no-restore
            if ($LASTEXITCODE -ne 0) { throw "$id consumer build failed: $LASTEXITCODE" }
            $output = Join-Path $consumer 'bin' $Configuration 'net10.0'
            foreach ($entry in $content) {
                $relative = $entry.FullName.Substring($contentRoot.Length)
                $file = Join-Path $output $relative
                if (!(Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -ne $entry.Length) {
                    throw "$id deployment file was not copied intact to consumer output: $relative"
                }
            }
            [xml] $manifest = Get-Content (Join-Path $output 'FabricHealerPkg' 'ServiceManifest.xml') -Raw
            $entryPoint = $manifest.ServiceManifest.CodePackage.EntryPoint.ExeHost.Program
            if ($entryPoint -ne $program -or !(Test-Path (Join-Path $output 'FabricHealerPkg' 'Code' $entryPoint))) {
                throw "$id consumer output does not satisfy the Service Fabric executable entry point."
            }
            Write-Host "PASS $id - $($content.Count) deployment files copied intact."
        }
        finally {
            $zip.Dispose()
        }
    }
}
