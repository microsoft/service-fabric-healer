The plugin model for FabricHealer (FH) allows for a customer to hook a plugin to FH and do custom work when FH starts. All plugins must implement ICustomServiceInitializer.

1. Create a .NET 6 Library project.

2. Write a custom worker!

	E.g., create a new class file, MyCustomWorker.cs.

```C#
	using System.Threading.Tasks;
	using FabricHealer.Interfaces;

	namespace FabricHealer.CustomWorker;

	[assembly: CustomServiceInitializer(typeof(MyCustomWorker))]
	public class MyCustomWorker : ICustomServiceInitializer
	{
		public Task InitializeAsync()
		{
			//custom worker code
		}
	}
```

3. Build your custom worker project, drop the output dll and *ALL* of its dependencies, both managed and native (this is *very* important), into the Data/Plugins folder in FabricHealer/PackageRoot. 
   You can place your plugin dll and all of its dependencies in its own (*same*) folder under the Plugins directory (useful if you have multiple plugins). 
   Again, ALL plugin dll dependencies (and their dependencies, if any) need to live in the *same* folder as the plugin dll.


4. Test your code and Ship it!