The plugin model for FabricHealer (FH) allows for a customer to hook a plugin to FH and do custom work during FH startup. All custom work plugins must implement ICustomServiceInitializer and must include the Plugin attribute.

1. Create a .NET 8 Library project.

2. Install the Microsoft.ServiceFabricApps.FabricHealer.Windows.SelfContained NuGet package from https://www.nuget.org/profiles/ServiceFabricApps as the version of FabricHealer you are deploying.
  E.g., 1.2.15 if you are going to deploy FH 1.2.15.


3. Write a custom worker!

	E.g., create a new class file, MyCustomWorker.cs.

```C#
	using System.Threading.Tasks;
	using FabricHealer.Interfaces;

	namespace FabricHealer.CustomWorker;

	[assembly: Plugin(typeof(MyCustomWorker))]
	public class MyCustomWorker : ICustomServiceInitializer
	{
		public Task InitializeAsync()
		{
			//custom worker code
		}
	}
```

4. Build your custom worker project, drop the output dll and *ALL* of its dependencies, both managed and native (this is *very* important), into the Data/Plugins folder in FabricHealer/PackageRoot. 
   You can place your plugin dll and all of its dependencies in its own (*same*) folder under the Plugins directory (useful if you have multiple plugins). 
   Again, ALL plugin dll dependencies (and their dependencies, if any) need to live in the *same* folder as the plugin dll.

   Also make sure that you set EnableCustomServiceInitializers to true in the ApplicationManifest.xml file so that FH knows to look for these plugins.


5. Test your code and Ship it!

If you want to build your own nupkg from FH source, then:

Open a PowerShell console, navigate to the top level directory of the FH repo (in this example, C:\Users\me\source\repos\service-fabric-healer):

cd C:\Users\me\source\repos\service-fabric-healer
./Build-FabricHealer
./Build-NugetPackages

The output from the above commands contains FabricHealer platform-specific nupkgs and a package you have to use for plugin authoring named Microsoft.ServiceFabricApps.FabricHealer.Windows.SelfContained.1.2.15.nupkg. Nupkg files from above command would be located in 
C:\Users\me\source\repos\service-fabric-healer\bin\release\FabricHealer\Nugets.