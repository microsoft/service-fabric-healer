# Development

## Build and package

Use PowerShell 7 and an SDK compatible with [global.json](../global.json).
`latestFeature` allows newer installed SDK feature bands in the same .NET 10.0
line while retaining the configured minimum version.

```powershell
dotnet build FabricHealer.Managed.slnf -c Release
if ($LASTEXITCODE -ne 0) { throw "Managed build failed." }

foreach ($rid in 'win-x64', 'win-arm64', 'linux-x64') {
    ./Build-FabricHealer.ps1 -RuntimeId $rid
}
./Build-SFPKGs.ps1
./Build-NugetPackages.ps1
./Build-FHProxyNupkg.ps1
./Test-DeploymentNuget.ps1
```

The publish script produces self-contained and framework-dependent trees.
NuGet metadata is generated from the projects by `dotnet pack`; the deployment
consumer checks verify the restored file trees and executable entry points.
See [deployment requirements](Deployment/Deployment.md#requirements) before
selecting a runtime or deployment platform.

## Compatibility

The host, TelemetryLib, and proxy provide separate `net8.0` and `net10.0` assets.
The host's .NET 8 output is a plugin compatibility asset, not its deployment
target. The current sample, tests, and deployed host use .NET 10; the Visual
Studio application project also selects that target.

Keep these assembly identities stable for existing binaries:

| Assembly | Version |
|---|---|
| FabricHealer | 1.3.2.0 |
| TelemetryLib | 2.2.0.0 |
| FabricHealerProxy | 1.0.0.0 |

File/product/package versions advance independently. Do not put .NET 10 binaries
under `lib/net8.0`. The loader shares contract assemblies and resolves transitive
references lazily so optional SDK references do not introduce an incidental WCF
dependency.

## Plugin compatibility tests

These tests load and execute both the unchanged .NET 8 sample and the current
.NET 10 sample without a cluster. From the repository root, create a separate
baseline checkout at the same commit used by [CI](../.github/workflows/build.yml):

```powershell
$repo = (Get-Location).Path
$baseline = Join-Path (Split-Path $repo -Parent) 'service-fabric-healer-net8-baseline'
git clone --no-checkout --filter=blob:none https://github.com/microsoft/service-fabric-healer.git $baseline
if ($LASTEXITCODE -ne 0) { throw "Baseline clone failed; choose a new directory." }
git -C $baseline switch --detach c12075eb5ed5f2e6a86872657a73ebfcfcfd5fa5
if ($LASTEXITCODE -ne 0) { throw "Baseline checkout failed." }

dotnet build (Join-Path $baseline 'SampleHealerPlugin\SampleHealerPlugin.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "Baseline plugin build failed." }
dotnet build FabricHealer.Managed.slnf -c Release
if ($LASTEXITCODE -ne 0) { throw "Managed build failed." }

$env:FH_LEGACY_PLUGIN_PATH = Join-Path $baseline 'SampleHealerPlugin\bin\Release\net8.0\SampleHealerPlugin.dll'
$env:FH_CURRENT_PLUGIN_PATH = Join-Path $repo 'SampleHealerPlugin\bin\Release\net10.0\SampleHealerPlugin.dll'
dotnet test FHTest\FHTest.csproj -c Release --no-build --filter FullyQualifiedName~PluginCompatibilityTests --logger trx
if ($LASTEXITCODE -ne 0) { throw "Plugin compatibility tests failed." }
```

Both variables must name existing plugin DLLs. Use a fresh baseline directory;
do not modify the pinned baseline source or retarget it.

## Cluster-dependent tests

Run the remaining FHTest cases only on a task-created disposable Azure test host.
They manipulate repair tasks, restart processes, and delete files. Set
`FH_DISPOSABLE_CLUSTER=1` only there; it is required before the destructive class
creates its FabricClient. Do not create a workstation cluster or target a shared
cluster. Upgrade validation requires a supported multi-node cluster.

Building FHTest regenerates the self-contained .NET 10 TestApp42 fixture from
[TestWorkloads](../TestWorkloads). Publishing is skipped during IDE design-time
builds.

## Local Guan integration

`GuanPackageVersion` defaults to the published dependency in
[Directory.Build.props](../Directory.Build.props). To validate a rebuilt Guan
package, restore with a temporary local NuGet feed and an MSBuild
`GuanPackageVersion` override. Restore again without that override before
packaging or committing. Keep private feeds and unpublished consumer dependencies
out of the repository; package publication requires approval.
