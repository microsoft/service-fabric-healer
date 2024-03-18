The plugin model for FabricHealer (FH) allows for a customer to attach a plugin to FH and perform custom repairs.

1. Create a .NET 6 Library project.

2. Install the Microsoft.ServiceFabricApps.FabricHealer.Windows.SelfContained NuGet package from https://www.nuget.org/profiles/ServiceFabricApps as the version of FabricHealer you are deploying.
  E.g., 1.2.13 if you are going to deploy FH 1.2.13.

3. Write a custom repair class!

  E.g., create a new class called MyRepairPredicateType.cs.

```C#
using System.Globalization;
using Guan.Logic;
using FabricHealer.Utilities;

namespace FabricHealer.SamplePlugins
{

    public class MyRepairPredicateType : PredicateType
    {
        private static MyRepairPredicateType Instance;
        private static SampleTelemetryData RepairData;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

            }

            protected override async Task<bool> CheckAsync()
            {
                //implement the action you want to perform in the custom repair.
                //you can check the FabricHealer\Repair\Guan path for more detailed examples
            }
        }

        public static MyRepairPredicateType Singleton(string name, SampleTelemetryData repairData)
        {
            RepairData = repairData;
            return Instance ??= new MyRepairPredicateType(name);
        }

        private MyRepairPredicateType(string name)
                 : base(name, true, 1)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}


```

4. Create a [RepairTypeName]Startup.cs file with this format (e.g., MyRepairPredicateType is the name of your plugin class.). All Startup classes must implement the IRepairPredicateType interface.

```C#
using FabricHealer;
using FabricHealer.Interfaces;
using FabricHealer.SamplePlugins;
using Guan.Logic;
using FabricHealer.Utilities;

[assembly: RepairPredicateType(typeof(MyRepairPredicateTypeStartup))]
namespace FabricHealer.SamplePlugins
{
    public class MyRepairPredicateTypeStartup : IRepairPredicateType
    {
        public void RegisterToPredicateTypesCollection(FunctorTable functorTable, string serializedRepairData)
        {
            JsonSerializationUtility.TryDeserializeObject(serializedRepairData, out SampleTelemetryData repairData);
            functorTable.Add(MyRepairPredicateType.Singleton("SampleRepair", repairData));
        }
    }
}
```

5. You must also then employ the new repair rule. So based on the example files above you will need to add the logic programming to a guan file. The guan files are in the FabricHealer\Repair\Guan directory. For example, based on the lines of code above,

```C#
Mitigate(some argument) :- [some logical expresion], SampleRepair("repair rule based on plugin").
```



6. Build your custom worker project, drop the output dll and *ALL* of its dependencies, both managed and native (this is *very* important), into the Data/Plugins folder in FabricHealer/PackageRoot. 
   You can place your plugin dll and all of its dependencies in its own (*same*) folder under the Plugins directory (useful if you have multiple plugins). 
   Again, ALL plugin dll dependencies (and their dependencies, if any) need to live in the *same* folder as the plugin dll.


7. Test your code and Ship it!

If you want to build your own nupkg from FH source, then:

Open a PowerShell console, navigate to the top level directory of the FH repo (in this example, C:\Users\me\source\repos\service-fabric-healer):

cd C:\Users\me\source\repos\service-fabric-healer
./Build-FabricHealer
./Build-NugetPackages

The output from the above commands contains FabricHealer platform-specific nupkgs and a package you have to use for plugin authoring named Microsoft.ServiceFabricApps.FabricHealer.Windows.SelfContained.1.2.13.nupkg. Nupkg files from above command would be located in 
C:\Users\me\source\repos\service-fabric-healer\bin\release\FabricHealer\Nugets.

	
	
