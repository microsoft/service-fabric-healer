param(
    [ValidateSet("win-x64", "win-arm64", "linux-x64")]
    [string] $RuntimeId = "win-x64",
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
foreach ($framework in @("net8.0", "net10.0")) {
    dotnet build "$PSScriptRoot\FabricHealerProxy\FabricHealerProxy.csproj" -f $framework -c $Configuration -r $RuntimeId
    if ($LASTEXITCODE -ne 0) { throw "FabricHealerProxy $framework build failed: $LASTEXITCODE" }
}
