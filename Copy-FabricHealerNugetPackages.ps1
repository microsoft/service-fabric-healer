try
{
	[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition
	Write-Host $scriptPath
	powershell -NonInteractive -NoProfile -ExecutionPolicy Bypass -File "$scriptPath\Build-FabricHealer.ps1"
	powershell -NonInteractive -NoProfile -ExecutionPolicy Bypass -File "$scriptPath\Build-NugetPackages.ps1"
	Remove-Item -Path $scriptPath\packages\microsoft.servicefabricapps.fabrichealer.windows.selfcontained -Recurse -Force
	Copy-Item -Path "$scriptPath\bin\release\FabricHealer\Nugets\Microsoft.ServiceFabricApps.FabricHealer.Windows.SelfContained.*" -Destination "$scriptPath\packages"
	dotnet restore "$scriptPath\SampleHealerPlugin\SampleHealerPlugin.csproj"
}
catch
{
	Write-Host $_.Exception.Message
	exit 1
}