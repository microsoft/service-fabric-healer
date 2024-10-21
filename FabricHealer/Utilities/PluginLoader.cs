using FabricHealer.Interfaces;
using FabricHealer.Utilities.Telemetry;
using Guan.Logic;
using McMaster.NETCore.Plugins;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace FabricHealer.Utilities
{
    public class FabricHealerPluginLoader
    {
        private static readonly IDictionary<IRepairPredicateType, IList<PredicateType>> PluginToPredicateTypesMap = new Dictionary<IRepairPredicateType, IList<PredicateType>>();
        private static readonly HashSet<ICustomServiceInitializer> CustomServiceInitializers = new();

        public static async Task InitializePluginsAsync(CancellationToken cancellationToken)
        {
            foreach (var customServiceInitializer in FabricHealerPluginLoader.CustomServiceInitializers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await customServiceInitializer.InitializeAsync();
            }
        }

        public static void LoadPluginPredicateTypes()
        {
            foreach (var repairPredicateType in FabricHealerPluginLoader.PluginToPredicateTypesMap.Keys)
            {
                var serviceCollection = new ServiceCollection();
                repairPredicateType.LoadPredicateTypes(serviceCollection);
                var predicateTypes = serviceCollection.BuildServiceProvider().GetServices<PredicateType>();
                foreach (var predicateType in predicateTypes)
                {
                    FabricHealerPluginLoader.PluginToPredicateTypesMap[repairPredicateType].Add((PredicateType)predicateType);
                }
            }
        }


        public static void RegisterPredicateTypes(FunctorTable functorTable, string serializedRepairData)
        {
            foreach (var plugin in FabricHealerPluginLoader.PluginToPredicateTypesMap.Keys)
            {
                foreach (var predicateType in FabricHealerPluginLoader.PluginToPredicateTypesMap[plugin])
                {
                    if(predicateType is not IPredicateType predicate)
                    {
                        throw new Exception($"{predicateType.GetType().FullName} must implement IPredicateType.");
                    }
                    predicate.SetRepairData(serializedRepairData);
                    functorTable.Add(predicateType);
                }
            }
        }

        public static void LoadPlugins(ServiceContext serviceContext)
        {
            string pluginsDir = Path.Combine(serviceContext.CodePackageActivationContext.GetDataPackageObject("Data").Path, "Plugins");

            if (!Directory.Exists(pluginsDir))
            {
                return;
            }

            string[] pluginDlls = Directory.GetFiles(pluginsDir, "*.dll", SearchOption.AllDirectories);
            PluginLoader[] pluginLoaders = new PluginLoader[pluginDlls.Length];

            if (pluginDlls.Length == 0)
            {
                return;
            }

            Type[] sharedTypes = { typeof(IRepairPredicateType), typeof(PluginAttribute), typeof(IPredicateType), typeof(ICustomServiceInitializer) };
            string dll = "";

            for (int i = 0; i < pluginDlls.Length; ++i)
            {
                dll = pluginDlls[i];
                var loader = PluginLoader.CreateFromAssemblyFile(dll, sharedTypes, a => a.IsUnloadable = false);
                pluginLoaders[i] = loader;
            }

            foreach (var pluginLoader in pluginLoaders)
            {
                try
                {
                    // If your plugin has native library dependencies (that's fine), then we will land in the catch (BadImageFormatException).
                    // This is by design. The Managed FH plugin assembly will successfully load, of course.
                    var pluginAssembly = pluginLoader.LoadDefaultAssembly();

                    var attributes = pluginAssembly.GetCustomAttributes<PluginAttribute>();

                    foreach (var attribute in attributes)
                    {
                        if (attribute != null)
                        {
                            if (Activator.CreateInstance(attribute.PluginType) is IRepairPredicateType repairPredicateType)
                            {
                                FabricHealerPluginLoader.PluginToPredicateTypesMap.Add(repairPredicateType, new List<PredicateType>());
                            }
                            else if (Activator.CreateInstance(attribute.PluginType) is ICustomServiceInitializer customServiceInitializer)
                            {
                                FabricHealerPluginLoader.CustomServiceInitializers.Add(customServiceInitializer);
                            }
                        }
                    }
                }
                catch (Exception e) when (e is ArgumentException or BadImageFormatException or IOException)
                {
                    string error = $"Plugin dll {dll} could not be loaded. Exception - {e.Message}.";

                    HealthReport healthReport = new()
                    {
                        AppName = new Uri($"{serviceContext.CodePackageActivationContext.ApplicationName}"),
                        EmitLogEvent = true,
                        HealthMessage = error,
                        EntityType = EntityType.Application,
                        HealthReportTimeToLive = TimeSpan.FromMinutes(10),
                        State = System.Fabric.Health.HealthState.Warning,
                        Property = "FabricHealerInitializerLoadError",
                        SourceId = $"FabricHealerService-{serviceContext.NodeContext.NodeName}",
                        NodeName = serviceContext.NodeContext.NodeName,
                    };

                    FabricHealthReporter healerHealth = new(FabricHealerManager.RepairLogger);
                    healerHealth.ReportHealthToServiceFabric(healthReport);

                    FabricHealerManager.RepairLogger.LogWarning($"Handled exception in FabricHealerPluginLoader.LoadPlugins: {e.Message}. {error}");
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    FabricHealerManager.RepairLogger.LogError($"Unhandled exception in FabricHealerPluginLoader.LoadPlugins: {e.Message}");
                    throw;
                }
            }
        }
    }
}
