param([string] $Configuration = "Release")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = "$PSScriptRoot\FHTest\obj\TestApp42"
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$root\Release" | Out-Null
Copy-Item "$PSScriptRoot\TestWorkloads\TestApp42\*" "$root\Release" -Recurse

dotnet publish "$PSScriptRoot\TestWorkloads\ChildProcessCreator\ChildProcessCreator.csproj" -c $Configuration -r win-x64 --self-contained false -o "$root\Release\ChildProcessCreatorPkg\Code"
if ($LASTEXITCODE -ne 0) { throw "ChildProcessCreator publish failed: $LASTEXITCODE" }

$archive = "$PSScriptRoot\FHTest\obj\TestApp42.zip"
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($root, $archive)
