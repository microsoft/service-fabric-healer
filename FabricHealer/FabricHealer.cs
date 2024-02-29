// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Interfaces;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using McMaster.NETCore.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ServiceFabric.Services.Runtime;

namespace FabricHealer
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    public sealed class FabricHealer : StatelessService
    {
        private readonly Logger logger;

        public FabricHealer(StatelessServiceContext context)
                : base(context)
        {
            logger = new Logger(nameof(FabricHealer));
        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            await LoadCustomServiceInitializers();

            using FabricHealerManager healerManager = new(Context, cancellationToken);
            
            // Blocks until StartAsync exits.
            await healerManager.StartAsync();
        }

        private async Task LoadCustomServiceInitializers()
        {
            string pluginsDir = Path.Combine(Context.CodePackageActivationContext.GetDataPackageObject("Data").Path, "Plugins");

            if (!Directory.Exists(pluginsDir))
            {
                return;
            }

            string[] pluginDlls = Directory.GetFiles(pluginsDir, "*.dll", SearchOption.AllDirectories);

            if (pluginDlls.Length == 0)
            {
                return;
            }

            PluginLoader[] pluginLoaders = new PluginLoader[pluginDlls.Length];
            Type[] sharedTypes = { typeof(CustomServiceInitializerAttribute), typeof(ICustomServiceInitializer)};
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
                    CustomServiceInitializerAttribute initializerAttribute = pluginAssembly.GetCustomAttribute<CustomServiceInitializerAttribute>();

                    object initializerObject = Activator.CreateInstance(initializerAttribute.InitializerType);

                    if (initializerObject is ICustomServiceInitializer customServiceInitializer)
                    {
                        await customServiceInitializer.InitializeAsync();
                    }
                    else
                    {
                        // This will bring down FO, which it should: This means your plugin is not supported. Fix your bug.
                        throw new Exception($"{initializerAttribute.InitializerType.FullName} must implement ICustomServiceInitializer.");
                    }
                }
                catch (Exception e) when (e is ArgumentException or BadImageFormatException or IOException or NullReferenceException)
                {
                    string error = $"Plugin dll {dll} could not be loaded. Exception - {e.Message}.";
                    switch (e)
                    {
                        case NullReferenceException:
                            error = $"{error} Please ignore if this dll is not supposed to implement the ICustomServiceInitializer.";
                            break;
                        case BadImageFormatException:
                            error = $"{error} Please ignore if this is a native dll.";
                            break;
                    }

                    HealthReport healthReport = new()
                    {
                        AppName = new Uri($"{Context.CodePackageActivationContext.ApplicationName}"),
                        EmitLogEvent = true,
                        HealthMessage = error,
                        EntityType = EntityType.Application,
                        HealthReportTimeToLive = TimeSpan.FromMinutes(10),
                        State = System.Fabric.Health.HealthState.Warning,
                        Property = "FabricHealerInitializerLoadError",
                        SourceId = $"FabricHealerService-{Context.NodeContext.NodeName}",
                        NodeName = Context.NodeContext.NodeName,
                    };

                    FabricHealthReporter healerHealth = new(logger);
                    healerHealth.ReportHealthToServiceFabric(healthReport);

                    logger.LogWarning($"handled exception in FabricHealerService Instance: {e.Message}. {error}");

                    continue;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    logger.LogError($"Unhandled exception in FabricHealerService Instance: {e.Message}");
                    throw;
                }
            }
        }

        // Graceful close.
        protected override async Task OnCloseAsync(CancellationToken cancellationToken)
        {
            await FabricHealerManager.TryClearExistingHealthReportsAsync();
            await FabricHealerManager.TryCleanUpOrphanedFabricHealerRepairJobsAsync(isClosing: true);
            _ = base.OnCloseAsync(cancellationToken);
        }

        // Stateless replica restarted.
        protected override void OnAbort()
        {
            _ = FabricHealerManager.TryClearExistingHealthReportsAsync().Wait(15000);
            _ = FabricHealerManager.TryCleanUpOrphanedFabricHealerRepairJobsAsync(isClosing: true).Wait(15000);
            base.OnAbort();
        }
    }
}
