using FabricHealer;
using FabricHealer.Interfaces;
using FabricHealer.Utilities;
using Guan.Logic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace FHTest
{
    [TestClass]
    public class PluginCompatibilityTests
    {
        public TestContext TestContext { get; set; }

        [TestMethod]
        [DataRow("FH_LEGACY_PLUGIN_PATH")]
        [DataRow("FH_CURRENT_PLUGIN_PATH")]
        public async Task Plugin_Loads_Initializes_Registers_And_Evaluates(string pathVariable)
        {
            string path = Environment.GetEnvironmentVariable(pathVariable);
            Assert.IsFalse(string.IsNullOrWhiteSpace(path), $"{pathVariable} must identify the plugin under test.");
            Assert.IsTrue(File.Exists(path), $"Plugin does not exist: {path}");
            TestContext.WriteLine(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            foreach (var contract in new[] { typeof(PluginAttribute), typeof(PredicateType),
                typeof(RepairFacts), typeof(System.Fabric.ServiceContext) })
            {
                Assembly loaded = contract.Assembly;
                TestContext.WriteLine($"{loaded.FullName}; file={loaded.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version}");
            }

            using var loader = FabricHealerPluginLoader.CreatePluginLoader(Path.GetFullPath(path));
            Assembly assembly = loader.LoadDefaultAssembly();
            var attributes = assembly.GetCustomAttributes<PluginAttribute>().ToArray();
            Assert.HasCount(2, attributes);

            var services = new ServiceCollection();
            int initializerCount = 0;
            int registrationCount = 0;
            foreach (var attribute in attributes)
            {
                object plugin = Activator.CreateInstance(attribute.PluginType);
                if (plugin is ICustomServiceInitializer initializer)
                {
                    await initializer.InitializeAsync();
                    initializerCount++;
                }
                if (plugin is IRepairPredicateType registration)
                {
                    registration.LoadPredicateTypes(services);
                    registrationCount++;
                }
            }

            Assert.AreEqual(1, initializerCount);
            Assert.AreEqual(1, registrationCount);
            using var provider = services.BuildServiceProvider();
            var predicates = provider.GetServices<PredicateType>().ToArray();
            Assert.HasCount(1, predicates);
            var functors = new FunctorTable();
            foreach (var predicate in predicates)
            {
                Assert.IsInstanceOfType<IPredicateType>(predicate);
                ((IPredicateType)predicate).SetRepairData("{}");
                functors.Add(predicate);
            }

            var module = Guan.Logic.Module.Parse("compatibility",
                new System.Collections.Generic.List<string> { "compatibility :- SampleRepair(' ')" }, functors);
            var modules = new ModuleProvider();
            modules.Add(module);
            var query = Query.Create("compatibility", new QueryContext(modules));
            Assert.IsNotNull(await query.GetNextAsync());
            Assert.IsNull(await query.GetNextAsync());
            Assert.IsFalse(AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name.StartsWith("System.ServiceModel", StringComparison.Ordinal)));
        }
    }
}
