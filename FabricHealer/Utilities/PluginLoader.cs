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
    internal class BasePluginLoader
    {
        protected Logger Logger { get; }
        protected ServiceContext ServiceContext { get; }


        protected BasePluginLoader(Logger logger, ServiceContext serviceContext)
        {
            this.Logger = logger;
            this.ServiceContext = serviceContext;
        }

        protected virtual Object GetPluginClassInstance(Assembly assembly)
        {
            throw new NotImplementedException();
        }

        protected virtual Task CallCustomAction(Object instance)
        {
            throw new NotImplementedException();
        }

        public async Task LoadPluginsAndCallCustomAction(Type attributeType, Type interfaceType)
        {
            string pluginsDir = Path.Combine(this.ServiceContext.CodePackageActivationContext.GetDataPackageObject("Data").Path, "Plugins");

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

            Type[] sharedTypes = { attributeType, interfaceType };
            string dll = "";

            for (int i = 0; i < pluginDlls.Length; ++i)
            {
                dll = pluginDlls[i];
                PluginLoader loader = PluginLoader.CreateFromAssemblyFile(dll, sharedTypes, a => a.IsUnloadable = false);
                pluginLoaders[i] = loader;
            }

            for (int i = 0; i < pluginLoaders.Length; ++i)
            {
                var pluginLoader = pluginLoaders[i];
                Assembly pluginAssembly;

                try
                {
                    // If your plugin has native library dependencies (that's fine), then we will land in the catch (BadImageFormatException).
                    // This is by design. The Managed FH plugin assembly will successfully load, of course.
                    pluginAssembly = pluginLoader.LoadDefaultAssembly();

                    object instance = this.GetPluginClassInstance(pluginAssembly);

                    await this.CallCustomAction(instance);
                }
                catch (Exception e) when (e is ArgumentException or BadImageFormatException or IOException or NullReferenceException)
                {
                    string error = $"Plugin dll {dll} could not be loaded. Exception - {e.Message}.";

                    HealthReport healthReport = new()
                    {
                        AppName = new Uri($"{this.ServiceContext.CodePackageActivationContext.ApplicationName}"),
                        EmitLogEvent = true,
                        HealthMessage = error,
                        EntityType = EntityType.Application,
                        HealthReportTimeToLive = TimeSpan.FromMinutes(10),
                        State = System.Fabric.Health.HealthState.Warning,
                        Property = "FabricHealerInitializerLoadError",
                        SourceId = $"FabricHealerService-{this.ServiceContext.NodeContext.NodeName}",
                        NodeName = this.ServiceContext.NodeContext.NodeName,
                    };

                    FabricHealthReporter healerHealth = new(FabricHealerManager.RepairLogger);
                    healerHealth.ReportHealthToServiceFabric(healthReport);

                    FabricHealerManager.RepairLogger.LogWarning($"handled exception in FabricHealerService Instance: {e.Message}. {error}");

                    continue;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    FabricHealerManager.RepairLogger.LogError($"Unhandled exception in FabricHealerService Instance: {e.Message}");
                    throw;
                }
            }
        }
    }

    internal class RepairPredicateTypePluginLoader : BasePluginLoader
    {
        private FunctorTable FunctorTable { get; }
        private string serializedRepairData { get; }

        public RepairPredicateTypePluginLoader(
            Logger logger,
            ServiceContext serviceContext,
            FunctorTable FunctorTable,
            string serializedRepairData)
            : base(logger, serviceContext)
        {
            this.FunctorTable = FunctorTable;
            this.serializedRepairData = serializedRepairData;
        }

        protected override Object GetPluginClassInstance(Assembly assembly)
        {
            RepairPredicateTypeAttribute attribute = assembly.GetCustomAttribute<RepairPredicateTypeAttribute>();
            return Activator.CreateInstance(attribute.CustomRepairPredicateType);
        }

        protected override Task CallCustomAction(Object instance)
        {
            if (instance is IRepairPredicateType customPredicateType)
            {
                customPredicateType.RegisterToPredicateTypesCollection(this.FunctorTable, this.serializedRepairData);
                return Task.CompletedTask;
            }

            // This will bring down FH, which it should: This means your plugin is not supported. Fix your bug.
            throw new Exception($"{instance.GetType().FullName} must implement IRepairPredicateType.");
        }
    }

    internal class ServiceInitializerPluginLoader : BasePluginLoader
    {
        public ServiceInitializerPluginLoader(Logger logger, ServiceContext serviceContext)
            : base(logger, serviceContext)
        {
        }

        protected override Object GetPluginClassInstance(Assembly assembly)
        {
            CustomServiceInitializerAttribute attribute = assembly.GetCustomAttribute<CustomServiceInitializerAttribute>();
            return Activator.CreateInstance(attribute.InitializerType);
        }

        protected override Task CallCustomAction(Object instance)
        {
            if (instance is ICustomServiceInitializer customServiceInitializer)
            {
                return customServiceInitializer.InitializeAsync();
            }

            // This will bring down FH, which it should: This means your plugin is not supported. Fix your bug.
            throw new Exception($"{instance.GetType().FullName} must implement ICustomServiceInitializer.");
        }
    }

    public class FabricHealerPluginLoader
    {
        private readonly ServiceContext _serviceContext;
        private static readonly IDictionary<IRepairPredicateType, IList<PredicateType>> PluginToPredicateTypesMap = new Dictionary<IRepairPredicateType, IList<PredicateType>>();
        private static readonly HashSet<ICustomServiceInitializer> CustomServiceInitializers = new();

        public FabricHealerPluginLoader(ServiceContext context)
        {
            this._serviceContext = context;
        }

        public async Task InitializePluginsAsync(CancellationToken cancellationToken)
        {
            foreach (var customServiceInitializer in FabricHealerPluginLoader.CustomServiceInitializers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await customServiceInitializer.InitializeAsync();
            }
        }

        public void LoadPluginPredicateTypes()
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


        public void RegisterPredicateTypes(FunctorTable functorTable, string serializedRepairData)
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

        public void LoadPlugins()
        {
            string pluginsDir = Path.Combine(this._serviceContext.CodePackageActivationContext.GetDataPackageObject("Data").Path, "Plugins");

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
                        AppName = new Uri($"{this._serviceContext.CodePackageActivationContext.ApplicationName}"),
                        EmitLogEvent = true,
                        HealthMessage = error,
                        EntityType = EntityType.Application,
                        HealthReportTimeToLive = TimeSpan.FromMinutes(10),
                        State = System.Fabric.Health.HealthState.Warning,
                        Property = "FabricHealerInitializerLoadError",
                        SourceId = $"FabricHealerService-{this._serviceContext.NodeContext.NodeName}",
                        NodeName = this._serviceContext.NodeContext.NodeName,
                    };

                    FabricHealthReporter healerHealth = new(FabricHealerManager.RepairLogger);
                    healerHealth.ReportHealthToServiceFabric(healthReport);

                    FabricHealerManager.RepairLogger.LogWarning($"handled exception in FabricHealerService Instance: {e.Message}. {error}");
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    FabricHealerManager.RepairLogger.LogError($"Unhandled exception in FabricHealerService Instance: {e.Message}");
                    throw;
                }
            }
        }
    }
}
