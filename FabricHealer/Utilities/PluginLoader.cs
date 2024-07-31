using FabricHealer.Interfaces;
using FabricHealer.Utilities.Telemetry;
using Guan.Logic;
using McMaster.NETCore.Plugins;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

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

        public virtual async Task LoadPluginsAndCallCustomAction(Type attributeType, Type interfaceType)
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
        protected FunctorTable FunctorTable;
        protected string serializedRepairData;

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

    /// <summary>
    /// 2 main differences between this class and RepairPredicateTypePluginLoader:
    /// 1. uses IRepairPredicateTypeV2 instead of IRepairPredicateType
    /// 2. caches the resolved Singleton methods for each plugin predicate type during first run
    /// and reuses them during subsequent runs.
    /// </summary>
    internal class RepairPredicateTypePluginLoaderV2 : RepairPredicateTypePluginLoader
    {
        private static Dictionary<string, MethodInfo> _predicateTypeSingletonMethods;

        public RepairPredicateTypePluginLoaderV2(
        Logger logger,
        ServiceContext serviceContext,
        FunctorTable FunctorTable,
        string serializedRepairData)
            : base(logger, serviceContext, FunctorTable, serializedRepairData)
        {
        }

        protected override Task CallCustomAction(Object instance)
        {
            RepairPredicateTypePluginLoaderV2._predicateTypeSingletonMethods = new();

            if (instance is IRepairPredicateTypeV2 customPredicateType)
            {
                var predicateTypes = customPredicateType.GetPredicateTypes();
                foreach (var predicateTypeName in predicateTypes.Keys)
                {
                    var predicateType = predicateTypes[predicateTypeName];

                    // assuming all plugin predicates have a public static Singleton method
                    MethodInfo methodInfo = predicateType.GetMethod("Singleton", BindingFlags.Public | BindingFlags.Static);

                    var methodParameters = new object[] { predicateTypeName, this.serializedRepairData };
                    RepairPredicateTypePluginLoaderV2._predicateTypeSingletonMethods.TryAdd(predicateTypeName, methodInfo);
                    var predicate = methodInfo!.Invoke(null, methodParameters);
                    this.FunctorTable.Add((Functor)predicate);
                }
                return Task.CompletedTask;
            }

            // This will bring down FH, which it should: This means your plugin is not supported. Fix your bug.
            throw new Exception($"{instance.GetType().FullName} must implement IRepairPredicateTypeV2.");
        }

        public override async Task LoadPluginsAndCallCustomAction(Type attributeType, Type interfaceType)
        {
            // first time load
            if (RepairPredicateTypePluginLoaderV2._predicateTypeSingletonMethods == null)
            {
                await base.LoadPluginsAndCallCustomAction(attributeType, interfaceType);
            }

            foreach (var predicateTypeSingletonMethod in RepairPredicateTypePluginLoaderV2._predicateTypeSingletonMethods!)
            {
                var methodParameters = new object[] { predicateTypeSingletonMethod.Key, this.serializedRepairData };
                var predicate = predicateTypeSingletonMethod.Value.Invoke(null, methodParameters);
                this.FunctorTable.Add((Functor)predicate);
            }
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
}
