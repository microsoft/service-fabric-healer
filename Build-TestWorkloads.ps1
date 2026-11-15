param([string] $Configuration = "Release")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Join-Path $PSScriptRoot 'FHTest' 'obj' 'TestApp42'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $root 'Release') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'TestWorkloads' 'TestApp42' '*') (Join-Path $root 'Release') -Recurse

$project = Join-Path $PSScriptRoot 'TestWorkloads' 'ChildProcessCreator' 'ChildProcessCreator.csproj'
$code = Join-Path $root 'Release' 'ChildProcessCreatorPkg' 'Code'
dotnet publish $project -c $Configuration -r win-x64 --self-contained true -o $code
if ($LASTEXITCODE -ne 0) { throw "ChildProcessCreator publish failed: $LASTEXITCODE" }

$archive = Join-Path $PSScriptRoot 'FHTest' 'obj' 'TestApp42.zip'
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($root, $archive)
