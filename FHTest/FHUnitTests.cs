// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Microsoft.VisualStudio.TestTools.UnitTesting;
using FabricHealer.Repair;
using Guan.Logic;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using System.Fabric;
using System.Threading;
using System.IO;
using System.Linq;
using FabricHealer.Utilities.Telemetry;
using FabricHealer.Utilities;
using FabricHealer;
using System.Fabric.Repair;
using System.Diagnostics;
using System.Fabric.Health;
using SupportedErrorCodes = FabricHealer.Utilities.SupportedErrorCodes;
using EntityType = FabricHealer.Utilities.Telemetry.EntityType;
using System.Xml;
using ServiceFabric.Mocks;
using static ServiceFabric.Mocks.MockConfigurationPackage;
using System.Fabric.Description;
using System.Fabric.Query;

namespace FHTest
{
    /// <summary>
    /// NOTE: Run these tests on your machine with a local SF dev cluster running.
    /// </summary>

    [TestClass]
    public class FHUnitTests
    {
        private static readonly Uri TestServiceName = new("fabric:/app/service");
        private static readonly FabricClient fabricClient = FabricHealerManager.FabricClientSingleton;
        private static ICodePackageActivationContext CodePackageContext = null;
        private static StatelessServiceContext TestServiceContext = null;
        private static readonly CancellationToken token = new();

        // This is the name of the node used on your local dev machine's SF cluster. If you customize this, then change it.
        private const string NodeName = "_Node_0";
        private const string FHProxyId = "FabricHealerProxy";

        [ClassInitialize]
#pragma warning disable IDE0060 // Remove unused parameter
        public static async Task TestClassStartUp(TestContext testContext)
#pragma warning restore IDE0060 // Remove unused parameter
        {
            if (!IsLocalSFRuntimePresent())
            {
                throw new Exception("Local dev cluster must be running to execute these tests correctly.");
            }

            /* SF runtime mocking care of ServiceFabric.Mocks by loekd.
               https://github.com/loekd/ServiceFabric.Mocks */

            // NOTE: Make changes in Settings.xml located in this project (FabricObserverTests) PackageRoot/Config directory to configure observer settings.
            string configPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "Settings.xml");
            ConfigurationPackage configPackage = BuildConfigurationPackageFromSettingsFile(configPath);

            CodePackageContext =
                new MockCodePackageActivationContext(
                        TestServiceName.AbsoluteUri,
                        "applicationType",
                        "Code",
                        "1.0.0.0",
                        Guid.NewGuid().ToString(),
                        @"C:\Log",
                        @"C:\Temp",
                        @"C:\Work",
                        "ServiceManifest",
                        "1.0.0.0")
                {
                    ConfigurationPackage = configPackage
                };

            TestServiceContext =
                new StatelessServiceContext(
                        new NodeContext(NodeName, new NodeId(0, 1), 0, "NodeType0", "TEST.MACHINE"),
                        CodePackageContext,
                        "FabricHealer.FabricHealerType",
                        TestServiceName,
                        null,
                        Guid.NewGuid(),
                        long.MaxValue);


            _ = FabricHealerManager.Instance(TestServiceContext, token);
            FabricHealerManager.ConfigSettings = new ConfigSettings(TestServiceContext)
            {
                TelemetryEnabled = false
            };

            var repairs = await fabricClient.RepairManager.GetRepairTaskListAsync();

            foreach (var repairTask in repairs)
            {
                try
                {
                    if (repairTask.State == RepairTaskState.Completed)
                    {
                        await fabricClient.RepairManager.DeleteRepairTaskAsync(repairTask.TaskId, repairTask.Version);
                    }
                    else
                    {
                        await FabricRepairTasks.CancelRepairTaskAsync(repairTask, token);
                    }

                    await Task.Delay(1000);
                }
                catch
                {

                }
            }

            await DeployTestApp42Async();
        }

        /* Helpers */

        private static ConfigurationPackage BuildConfigurationPackageFromSettingsFile(string configPath)
        {
            StringReader sreader = null;
            XmlReader xreader = null;

            try
            {
                if (string.IsNullOrWhiteSpace(configPath))
                {
                    return null;
                }

                string configXml = File.ReadAllText(configPath);

                // Safe XML pattern - *Do not use LoadXml*.
                XmlDocument xdoc = new() { XmlResolver = null };
                sreader = new StringReader(configXml);
                xreader = XmlReader.Create(sreader, new XmlReaderSettings { XmlResolver = null });
                xdoc.Load(xreader);

                var nsmgr = new XmlNamespaceManager(xdoc.NameTable);
                nsmgr.AddNamespace("sf", "http://schemas.microsoft.com/2011/01/fabric");
                var sectionNodes = xdoc.SelectNodes("//sf:Section", nsmgr);
                var configSections = new ConfigurationSectionCollection();

                if (sectionNodes != null)
                {
                    foreach (XmlNode node in sectionNodes)
                    {
                        ConfigurationSection configSection = CreateConfigurationSection(node?.Attributes?.Item(0).Value);
                        var sectionParams = xdoc.SelectNodes($"//sf:Section[@Name='{configSection.Name}']//sf:Parameter", nsmgr);

                        if (sectionParams != null)
                        {
                            foreach (XmlNode node2 in sectionParams)
                            {
                                ConfigurationProperty parameter = CreateConfigurationSectionParameters(node2?.Attributes?.Item(0).Value, node2?.Attributes?.Item(1).Value);
                                configSection.Parameters.Add(parameter);
                            }
                        }

                        configSections.Add(configSection);
                    }

                    var configSettings = CreateConfigurationSettings(configSections);
                    ConfigurationPackage configPackage = CreateConfigurationPackage(configSettings, configPath.Replace("\\Settings.xml", ""));
                    return configPackage;
                }
            }
            finally
            {
                sreader.Dispose();
                xreader.Dispose();
            }

            return null;
        }

        private static bool IsLocalSFRuntimePresent()
        {
            try
            {
                var ps = Process.GetProcessesByName("Fabric");
                return ps.Length != 0;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static async Task DeployTestApp42Async()
        {
            string appName = "fabric:/TestApp42";

            // If fabric:/TestApp42 is already installed, exit.
            var deployedTestApp =
                    await fabricClient.QueryManager.GetDeployedApplicationListAsync(
                            NodeName,
                            new Uri(appName),
            TimeSpan.FromSeconds(30),
                            token);

            if (deployedTestApp?.Count > 0)
            {
                return;
            }

            string appType = "TestApp42Type";
            string appVersion = "1.0.0";

            // Change this to suit your configuration (so, if you are on Windows and you installed SF on a different drive, for example).
            string imageStoreConnectionString = @"file:C:\SfDevCluster\Data\ImageStoreShare";
            string packagePathInImageStore = "TestApp42";
            string packagePathZip = Path.Combine(Environment.CurrentDirectory, "TestApp42.zip");
            string packagePath = Path.Combine(Environment.CurrentDirectory, "TestApp42", "Release");

            try
            {
                // Unzip the compressed HealthMetrics app package.
                System.IO.Compression.ZipFile.ExtractToDirectory(packagePathZip, "TestApp42", true);

                // Copy the HealthMetrics app package to a location in the image store.
                fabricClient.ApplicationManager.CopyApplicationPackage(imageStoreConnectionString, packagePath, packagePathInImageStore);

                // Provision the HealthMetrics application.          
                await fabricClient.ApplicationManager.ProvisionApplicationAsync(packagePathInImageStore);

                // Create HealthMetrics app instance.
                ApplicationDescription appDesc = new(new Uri(appName), appType, appVersion);
                await fabricClient.ApplicationManager.CreateApplicationAsync(appDesc);

                // This is a hack. Withouth this timeout, the deployed test services may not have populated the FC cache?
                // You may need to increase this value depending upon your dev machine? You'll find out..
                await Task.Delay(TimeSpan.FromSeconds(15));
            }
            catch (FabricException fe)
            {
                if (fe.ErrorCode == FabricErrorCode.ApplicationAlreadyExists)
                {
                    await fabricClient.ApplicationManager.DeleteApplicationAsync(new DeleteApplicationDescription(new Uri(appName)) { ForceDelete = true });
                    await DeployTestApp42Async();
                }
                else if (fe.ErrorCode == FabricErrorCode.ApplicationTypeAlreadyExists)
                {
                    var appList = await fabricClient.QueryManager.GetApplicationListAsync(new Uri(appName));
                    if (appList.Count > 0)
                    {
                        await fabricClient.ApplicationManager.DeleteApplicationAsync(new DeleteApplicationDescription(new Uri(appName)) { ForceDelete = true });
                    }
                    await fabricClient.ApplicationManager.UnprovisionApplicationAsync(appType, appVersion);
                    await DeployTestApp42Async();
                }
            }
        }

        private static async Task<bool> EnsureTestServicesExistAsync(string appName)
        {
            try
            {
                var services = await fabricClient.QueryManager.GetServiceListAsync(new Uri(appName));
                return services?.Count > 0;
            }
            catch (FabricElementNotFoundException)
            {

            }

            return false;
        }

        private static async Task RemoveTestApplicationsAsync()
        {
            string imageStoreConnectionString = @"file:C:\SfDevCluster\Data\ImageStoreShare";

            // TestApp42 \\

            if (await EnsureTestServicesExistAsync("fabric:/TestApp42"))
            {
                string appName = "fabric:/TestApp42";
                string appType = "TestApp42Type";
                string appVersion = "1.0.0";
                string serviceName1 = "fabric:/TestApp42/ChildProcessCreator";
                string packagePathInImageStore = "TestApp42";

                // Clean up the unzipped directory.
                fabricClient.ApplicationManager.RemoveApplicationPackage(imageStoreConnectionString, packagePathInImageStore);

                // Delete services.
                var deleteServiceDescription1 = new DeleteServiceDescription(new Uri(serviceName1));
                await fabricClient.ServiceManager.DeleteServiceAsync(deleteServiceDescription1);

                // Delete an application instance from the application type.
                var deleteApplicationDescription = new DeleteApplicationDescription(new Uri(appName));
                await fabricClient.ApplicationManager.DeleteApplicationAsync(deleteApplicationDescription);

                // Un-provision the application type.
                await fabricClient.ApplicationManager.UnprovisionApplicationAsync(appType, appVersion);
            }
        }

        [ClassCleanup]
        public static async Task TestClassCleanupAsync()
        {
            // Ensure FHProxy cleans up its health reports.
            FabricHealerProxy.Instance.Close();
            await RemoveTestApplicationsAsync();
            
            // Clean up all test repair tasks.
            var repairs = await fabricClient.RepairManager.GetRepairTaskListAsync();

            foreach (var repairTask in repairs)
            {
                try
                {
                    // Don't delete repairs unrelated to FabricHealer.
                    if (!repairTask.TaskId.StartsWith("FH"))
                    {
                        continue;
                    }

                    if (repairTask.State == RepairTaskState.Completed)
                    {
                        await fabricClient.RepairManager.DeleteRepairTaskAsync(
                                repairTask.TaskId,
                                repairTask.Version,
                                TimeSpan.FromMinutes(2),
                                token);
                    }
                    else
                    {
                        await FabricRepairTasks.CancelRepairTaskAsync(repairTask, token);
                    }

                    await Task.Delay(1000);
                }
                catch
                {

                }
            }

            await FabricHealerManager.TryClearExistingHealthReportsAsync();
            fabricClient.Dispose();
        }

        /* GuanLogic Tests 
           The tests below validate entity-specific logic rules and the successful scheduling of related local repair jobs. */


        [TestMethod]
        public async Task AllAppRules_EnsureWellFormedRules_QueryInitialized_Successful()
        {
            var partitions = await fabricClient.QueryManager.GetPartitionListAsync(new Uri("fabric:/TestApp42/ChildProcessCreator"));
            Guid partition = partitions[0].PartitionInformation.Id;

            // This will be the data used to create a repair task.
            var repairData = new TelemetryData
            {
                ApplicationName = "fabric:/TestApp42",
                EntityType = EntityType.Service,
                NodeName = NodeName,
                Code = SupportedErrorCodes.AppErrorMemoryMB,
                HealthState = HealthState.Warning,
                PartitionId = partition.ToString(),
                Source = $"AppObserver({SupportedErrorCodes.AppErrorMemoryMB})",
                Property = "TestApp42_ChildProcessCreator_MemoryMB",
                ProcessName = "ChildProcessCreator",
                ServiceName = "fabric:/TestApp42/ChildProcessCreator",
                Value = 1024.0
            };

            repairData.RepairPolicy = new RepairPolicy
            {
                RepairId = $"TestApp42_{SupportedErrorCodes.AppErrorMemoryMB}{NodeName}",
                AppName = repairData.ApplicationName,
                RepairIdPrefix = RepairConstants.FHTaskIdPrefix,
                NodeName = repairData.NodeName,
                Code = repairData.Code,
                HealthState = repairData.HealthState,
                ProcessName = repairData.ProcessName,
                ServiceName = repairData.ServiceName
            };

            var executorData = new RepairExecutorData
            {
                RepairPolicy = repairData.RepairPolicy
            };

            var file = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "LogicRules", "AppRules.guan");
            FabricHealerManager.CurrentlyExecutingLogicRulesFileName = "AppRules.guan";
            List<string> repairRules = FabricHealerManager.ParseRulesFile(await File.ReadAllLinesAsync(file, token));

            try
            {
                await TestInitializeGuanAndRunQuery(repairData, repairRules, executorData);
            }
            catch (GuanException ge)
            {
                throw new AssertFailedException(ge.Message, ge);
            }
        }

        [TestMethod]
        public async Task AllMachineRules_EnsureWellFormedRules_QueryInitialized_Successful()
        {
            // This will be the data used to create a repair task.
            var repairData = new TelemetryData
            {
                EntityType = EntityType.Machine,
                NodeName = NodeName,
                HealthState = HealthState.Error
            };

            repairData.RepairPolicy = new RepairPolicy
            {
                RepairId = $"Test42_MachineRepair{NodeName}",
                AppName = repairData.ApplicationName,
                RepairIdPrefix = RepairConstants.InfraTaskIdPrefix,
                NodeName = repairData.NodeName,
                HealthState = repairData.HealthState
            };

            var executorData = new RepairExecutorData
            {
                RepairPolicy = repairData.RepairPolicy
            };

            var file = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "LogicRules", "MachineRules.guan");
            FabricHealerManager.CurrentlyExecutingLogicRulesFileName = "MachineRules.guan";
            List<string> repairRules = FabricHealerManager.ParseRulesFile(await File.ReadAllLinesAsync(file, token));

            try
            {
                await TestInitializeGuanAndRunQuery(repairData, repairRules, executorData);
            }
            catch (GuanException ge)
            {
                throw new AssertFailedException(ge.Message, ge);
            }
        }

        [TestMethod]
        public async Task AllDiskRules_FolderSizeMB_Warning_EnsureWellFormedRules_QueryInitialized_Successful()
        {
            // This will be the data used to create a repair task.
            var repairData = new TelemetryData
            {
                EntityType = EntityType.Disk,
                NodeName = NodeName,
                Metric = SupportedMetricNames.FolderSizeMB,
                Code = SupportedErrorCodes.NodeWarningFolderSizeMB,
                HealthState = HealthState.Warning,
                Source = $"DiskObserver({SupportedErrorCodes.NodeWarningFolderSizeMB})",
                Property = $"{NodeName}_{SupportedMetricNames.FolderSizeMB.Replace(" ", string.Empty)}"
            };

            repairData.RepairPolicy = new RepairPolicy
            {
                RepairId = $"Test42_DiskRepair{NodeName}",
                AppName = repairData.ApplicationName,
                RepairIdPrefix = RepairConstants.FHTaskIdPrefix,
                NodeName = repairData.NodeName,
                Code = repairData.Code,
                HealthState = repairData.HealthState,
            };

            var executorData = new RepairExecutorData
            {
                RepairPolicy = repairData.RepairPolicy
            };

            var file = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "LogicRules", "DiskRules.guan");
            FabricHealerManager.CurrentlyExecutingLogicRulesFileName = "DiskRules.guan";
            List<string> repairRules = FabricHealerManager.ParseRulesFile(await File.ReadAllLinesAsync(file, token));

            try
            {
                await TestInitializeGuanAndRunQuery(repairData, repairRules, executorData);
            }
            catch (GuanException ge)
            {
                throw new AssertFailedException(ge.Message, ge);
            }
        }

        [TestMethod]
        public async Task AllDiskRules_DiskSpacePercent_Warning_EnsureWellFormedRules_QueryInitialized_Successful()
        {
            // This will be the data used to create a repair task.
            var repairData = new TelemetryData
            {
                EntityType = EntityType.Disk,
                NodeName = NodeName,
                Metric = SupportedMetricNames.DiskSpaceUsagePercentage,
                Code = SupportedErrorCodes.NodeWarningDiskSpacePercent,
                HealthState = HealthState.Warning,
                Source = $"DiskObserver({SupportedErrorCodes.NodeWarningDiskSpacePercent})",
                Property = $"{NodeName}_{SupportedMetricNames.DiskSpaceUsagePercentage.Replace(" ", string.Empty)}"
            };

            repairData.RepairPolicy = new RepairPolicy
            {
                RepairId = $"Test42_DiskRepair{NodeName}",
                AppName = repairData.ApplicationName,
                RepairIdPrefix = RepairConstants.FHTaskIdPrefix,
                NodeName = repairData.NodeName,
                Code = repairData.Code,
                HealthState = repairData.HealthState
            };

            var executorData = new RepairExecutorData
            {
                RepairPolicy = repairData.RepairPolicy
            };

            var file = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "LogicRules", "DiskRules.guan");
            FabricHealerManager.CurrentlyExecutingLogicRulesFileName = "DiskRules.guan";
            List<string> repairRules = FabricHealerManager.ParseRulesFile(await File.ReadAllLinesAsync(file, token));

            try
            {
                await TestInitializeGuanAndRunQuery(repairData, repairRules, executorData);
            }
            catch (GuanException ge)
            {
                throw new AssertFailedException(ge.Message, ge);
            }
        }

        [TestMethod]
        public async Task AllFabricNodeRules_EnsureWellFormedRules_QueryInitialized_Successful()
        {
            // This will be the data used to create a repair task.
            var repairData = new TelemetryData
            {
                EntityType = EntityType.Node,
                NodeName = NodeName,
                HealthState = HealthState.Error
            };

            repairData.RepairPolicy = new RepairPolicy
            {
                RepairId = $"Test42_FabricNodeRepair_{NodeName}",
                RepairIdPrefix = RepairConstants.FHTaskIdPrefix,
                NodeName = repairData.NodeName,
                HealthState = repairData.HealthState
            };

            var executorData = new RepairExecutorData
            {
                RepairPolicy = repairData.RepairPolicy
            };

            var file = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "LogicRules", "FabricNodeRules.guan");
            FabricHealerManager.CurrentlyExecutingLogicRulesFileName = "FabricNodeRules.guan";
            List<string> repairRules = FabricHealerManager.ParseRulesFile(await File.ReadAllLinesAsync(file, token));

            try
            {
                TimeSpan maxTestTime = TimeSpan.FromSeconds(30);

                // don't block here.
                _ = TestInitializeGuanAndRunQuery(repairData, repairRules, executorData);

                var repairTasks = await fabricClient.RepairManager.GetRepairTaskListAsync(
                                            RepairConstants.FHTaskIdPrefix, RepairTaskStateFilter.Active, null);

                Stopwatch timer = Stopwatch.StartNew();

                while (timer.Elapsed < maxTestTime)
                {
                    repairTasks = await fabricClient.RepairManager.GetRepairTaskListAsync(
                                            RepairConstants.FHTaskIdPrefix, RepairTaskStateFilter.Active, null);

                    if (!repairTasks.Any(r => r.Action == "RestartFabricNode"))
                    {
                        await Task.Delay(1000);
                        continue;
                    }

                    await FabricRepairTasks.CancelRepairTaskAsync(repairTasks.First(r => r.Action == "RestartFabricNode"), token);
                    return;
                }

                throw new InternalTestFailureException("FabricNode repair task did not get created with max test time of 30s.");
                
            }
            catch (GuanException ge)
            {
                throw new InternalTestFailureException(ge.Message, ge);
            }
            catch (FabricException fe)
            {
                throw new InternalTestFailureException(fe.Message, fe);
            }
        }

        [TestMethod]
        public async Task AllReplicaRules_EnsureWellFormedRules_QueryInitialized_Successful()
        {
            var partitions = await fabricClient.QueryManager.GetPartitionListAsync(new Uri("fabric:/TestApp42/ChildProcessCreator"));
            Guid partition = partitions[0].PartitionInformation.Id;
            var replicas = await fabricClient.QueryManager.GetReplicaListAsync(partition);
            Replica replica = replicas[0];
            long replicaId = replica.Id;

            // This will be the data used to create a repair task.
            var repairData = new TelemetryData
            {
                ApplicationName = "fabric:/TestApp42",
                EntityType = EntityType.Service,
                NodeName = NodeName,
                Code = SupportedErrorCodes.AppErrorMemoryMB,
                HealthState = HealthState.Warning,
                PartitionId = partition.ToString(),
                ReplicaId = replicaId,
                Source = $"AppObserver({SupportedErrorCodes.AppErrorMemoryMB})",
                Property = "TestApp42_ChildProcessCreator_MemoryMB",
                ProcessName = "ChildProcessCreator",
                ServiceName = "fabric:/TestApp42/ChildProcessCreator",
                Value = 1024.0
            };

            repairData.RepairPolicy = new RepairPolicy
            {
                RepairId = $"Test42_ReplicaRepair{NodeName}",
                AppName = repairData.ApplicationName,
                RepairIdPrefix = RepairConstants.FHTaskIdPrefix,
                NodeName = repairData.NodeName,
                Code = repairData.Code,
                HealthState = repairData.HealthState,
                ProcessName = repairData.ProcessName,
                ServiceName = repairData.ServiceName,
                MaxTimePostRepairHealthCheck = TimeSpan.FromSeconds(1)
            };

            var executorData = new RepairExecutorData
            {
                RepairPolicy = repairData.RepairPolicy
            };

            var file = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "LogicRules", "ReplicaRules.guan");
            FabricHealerManager.CurrentlyExecutingLogicRulesFileName = "ReplicaRules.guan";
            List<string> repairRules = FabricHealerManager.ParseRulesFile(await File.ReadAllLinesAsync(file, token));

            try
            {
                await TestInitializeGuanAndRunQuery(repairData, repairRules, executorData);
            }
            catch (GuanException ge)
            {
                throw new AssertFailedException(ge.Message, ge);
            }
        }

        [TestMethod]
        public async Task AllSystemServiceRules_EnsureWellFormedRules_QueryInitialized_Successful()
        {
            var repairData = new TelemetryData
            {
                ApplicationName = "fabric:/System",
                EntityType = EntityType.Partition,
                PartitionId = Guid.NewGuid().ToString(),
                NodeName = NodeName,
                HealthState = HealthState.Warning
            };

            repairData.RepairPolicy = new RepairPolicy
            {
                RepairId = $"Test42_SystemServiceRepair{NodeName}",
                AppName = repairData.ApplicationName,
                RepairIdPrefix = RepairConstants.FHTaskIdPrefix,
                NodeName = repairData.NodeName,
                Code = repairData.Code,
                HealthState = repairData.HealthState,
                ProcessName = repairData.ProcessName,
                ServiceName = repairData.ServiceName,
                MaxTimePostRepairHealthCheck = TimeSpan.FromSeconds(1)
            };

            var executorData = new RepairExecutorData
            {
                RepairPolicy = repairData.RepairPolicy
            };

            var file = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "LogicRules", "SystemServiceRules.guan");
            List<string> repairRules = FabricHealerManager.ParseRulesFile(await File.ReadAllLinesAsync(file, token));
            FabricHealerManager.CurrentlyExecutingLogicRulesFileName = "SystemServiceRules.guan";

            try
            {
                await TestInitializeGuanAndRunQuery(repairData, repairRules, executorData);
            }
            catch (GuanException ge)
            {
                throw new AssertFailedException(ge.Message, ge);
            }
        }

        // This test ensures your test rules housed in testrules_wellformed file or in fact correct.
        // Ensures that not specifying EntityType also works given the facts that are provided in the TelemetryData instance.
        [TestMethod]
        public async Task TestGuanLogicRule_GoodRule_QueryInitialized()
        {
            string testRulesFilePath = Path.Combine(Environment.CurrentDirectory, "testrules_wellformed.guan");
            string[] rules = await File.ReadAllLinesAsync(testRulesFilePath, token);
            FabricHealerManager.CurrentlyExecutingLogicRulesFileName = "testrules_wellformed.guan";
            List<string> repairRules = FabricHealerManager.ParseRulesFile(rules);
            var partitions = await fabricClient.QueryManager.GetPartitionListAsync(new Uri("fabric:/TestApp42/ChildProcessCreator"));
            Guid partition = partitions[0].PartitionInformation.Id;

            // This will be the data used to create a repair task.
            var repairData = new TelemetryData
            {
                ApplicationName = "fabric:/TestApp42",
                EntityType = EntityType.Service,
                NodeName = NodeName,
                Code = SupportedErrorCodes.AppErrorMemoryMB,
                HealthState = HealthState.Warning,
                PartitionId = partition.ToString(),
                Source = $"AppObserver({SupportedErrorCodes.AppErrorMemoryMB})",
                Property = "TestApp42_ChildProcessCreator_MemoryMB",
                ProcessName = "ChildProcessCreator",
                ServiceName = "fabric:/TestApp42/ChildProcessCreator",
                Value = 1024.0
            };

            repairData.RepairPolicy = new RepairPolicy
            {
                RepairId = $"Test42_{SupportedErrorCodes.AppErrorMemoryMB}{NodeName}",
                AppName = repairData.ApplicationName,
                RepairIdPrefix = RepairConstants.FHTaskIdPrefix,
                NodeName = repairData.NodeName,
                Code = repairData.Code,
                HealthState = repairData.HealthState,
                ProcessName = repairData.ProcessName,
                ServiceName = repairData.ServiceName,
                MaxTimePostRepairHealthCheck = TimeSpan.FromSeconds(1)
            };

            var executorData = new RepairExecutorData
            {
                RepairPolicy = repairData.RepairPolicy
            };

            try
            {
                await TestInitializeGuanAndRunQuery(repairData, repairRules, executorData);
            }
            catch (GuanException ge)
            {
                throw new AssertFailedException(ge.Message, ge);
            }
        }

        // This test ensures your test rules housed in testrules_malformed file or in fact incorrect.
        [TestMethod]
        public async Task TestGuanLogicRule_BadRule_ShouldThrowGuanException()
        {
            string[] rules = await File.ReadAllLinesAsync(Path.Combine(Environment.CurrentDirectory, "testrules_malformed.guan"), token);
            List<string> repairAction = FabricHealerManager.ParseRulesFile(rules);

            var repairData = new TelemetryData
            {
                ApplicationName = "fabric:/test0",
                EntityType = EntityType.Service,
                NodeName = NodeName,
                Metric = "Memory",
                HealthState = HealthState.Warning,
                Code = SupportedErrorCodes.AppErrorMemoryMB,
                ServiceName = "fabric:/test0/service0",
                Value = 42,
                ReplicaId = default,
                PartitionId = default,
            };

            repairData.RepairPolicy = new RepairPolicy
            {
                RepairId = $"Test42_{SupportedErrorCodes.AppErrorMemoryMB}{NodeName}",
                AppName = repairData.ApplicationName,
                RepairIdPrefix = RepairConstants.FHTaskIdPrefix,
                NodeName = repairData.NodeName,
                Code = repairData.Code,
                HealthState = repairData.HealthState,
                ProcessName = repairData.ProcessName,
                ServiceName = repairData.ServiceName
            };

            var executorData = new RepairExecutorData
            {
                RepairPolicy = repairData.RepairPolicy
            };

            await Assert.ThrowsExceptionAsync<GuanException>(async () => { await TestInitializeGuanAndRunQuery(repairData, repairAction, executorData); });
        }

        // Ensure that all machine repair escalations (repair actions) are scheduled.
        // The test logic program used here includes the following rule to also test FH scheduling behavior when a
        // watchdog service creates an SF health event with specific source and/or property facts:
        // Mitigate(Property=?property, Source=?source) :- notmatch(?source, "TestMachineWatchdog") && notmatch(?property, "WER 55"), !.
        // You can use either Source or Property as a constraint in the logic rule. The TelemetryData instance (repairData) in this function specifies both,
        // but the logic rule requires that only one of the facts be present. If neither are present, then stop processing rules (ending with a ! (cut)).
        [TestMethod]
        public async Task Ensure_MachineRepair_ErrorDetected_RepairJobCreated_AllEscalations()
        {
            string testRulesFilePath = 
                Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "LogicRules", "MachineRules.guan");
            string[] rules = await File.ReadAllLinesAsync(testRulesFilePath, token);
            FabricHealerManager.CurrentlyExecutingLogicRulesFileName = "MachineRules.guan";
            List<string> repairRules = FabricHealerManager.ParseRulesFile(rules);
            int escalationCount = 4; // reboot, reimage, heal, triage.
            RepairTaskList repairTasks = null;

            for (int i = 0; i < escalationCount; i++)
            {
                var repairData = new TelemetryData
                {
                    EntityType = EntityType.Machine,
                    // In practice, this fact comes from an SF HealthEvent (HealthInformation.SourceId).
                    Source = "EventLogWatchdog007",
                    // In practice, this fact comes from an SF HealthEvent (HealthInformation.Property).
                    Property = "EventLogError WER 55",
                    HealthState = HealthState.Error,
                    NodeName = NodeName
                };

                repairData.RepairPolicy = new RepairPolicy
                {
                    RepairIdPrefix = RepairConstants.InfraTaskIdPrefix,
                    NodeName = repairData.NodeName,
                    HealthState = repairData.HealthState,
                    MaxTimePostRepairHealthCheck = TimeSpan.FromSeconds(1)
                };

                var executorData = new RepairExecutorData
                {
                    RepairPolicy = repairData.RepairPolicy
                };
                
                await TestInitializeGuanAndRunQuery(repairData, repairRules, executorData);

                // Allow time for repair job creation.
                await Task.Delay(5000, token);

                repairTasks = await fabricClient.RepairManager.GetRepairTaskListAsync(
                                        RepairConstants.InfraTaskIdPrefix, RepairTaskStateFilter.Active, null);

                Assert.IsTrue(repairTasks.Any());
                await FabricRepairTasks.CancelRepairTaskAsync(repairTasks.First(), token);
            }

            // Verify that all the specified escalations ran. \\

            // Allow time for repair job cancellation.
            await Task.Delay(5000, token);

            repairTasks = await fabricClient.RepairManager.GetRepairTaskListAsync(
                                    RepairConstants.InfraTaskIdPrefix, RepairTaskStateFilter.Completed, null);
                
            Assert.IsTrue(repairTasks.Any());

            var testRepairTasks = repairTasks.Where(
                    r => r.TaskId.StartsWith(RepairConstants.InfraTaskIdPrefix, StringComparison.OrdinalIgnoreCase) 
                        && r.ResultStatus == RepairTaskResult.Cancelled);

            Assert.IsTrue(testRepairTasks.Any());
           
            testRepairTasks =
                    repairTasks.Where(
                    r => DateTime.UtcNow.Subtract(r.CompletedTimestamp.Value) < TimeSpan.FromMinutes(2)
                        && RepairTaskEngine.MatchSubstring(RepairTaskEngine.NodeRepairActionSubstrings, r.Action));

            Assert.IsTrue(testRepairTasks.Count() == escalationCount);

            // Clean up.
            foreach (RepairTask repair in testRepairTasks)
            {
                await fabricClient.RepairManager.DeleteRepairTaskAsync(repair.TaskId, repair.Version);
            }
        }

        // Ensure all machine repair escalations run, where the workflow is initiated by FabricHealerProxy.
        [TestMethod]
        public async Task Ensure_MachineRepair_ErrorDetected_RepairJobsCreated_AllEscalations_FHProxy()
        {
            // Create FabricHealerManager singleton (required).
            _ = FabricHealerManager.Instance(TestServiceContext, token);
            int escalationCount = 4; // reboot, reimage, heal, triage.
            RepairTaskList repairTasks = null;

            for (int i = 0; i < escalationCount; i++)
            {
                await FabricHealerProxy.Instance.RepairEntityAsync(WatchDogMachineRepairFacts, token);
                await FabricHealerManager.MonitorHealthEventsAsync();

                // Allow time for repair job creation.
                await Task.Delay(5000, token);

                repairTasks = await fabricClient.RepairManager.GetRepairTaskListAsync(
                                      RepairConstants.InfraTaskIdPrefix, RepairTaskStateFilter.Active, null);

                Assert.IsTrue(repairTasks.Any());

                await FabricRepairTasks.CancelRepairTaskAsync(repairTasks.First(), token);
            }

            // Verify that all the specified escalations ran. \\

            // Allow time for repair job cancellation.
            await Task.Delay(5000, token);

            repairTasks = await fabricClient.RepairManager.GetRepairTaskListAsync(
                                    RepairConstants.InfraTaskIdPrefix, RepairTaskStateFilter.Completed, null);
                
            Assert.IsTrue(repairTasks.Any());

            var testRepairTasks = repairTasks.Where(
                    r => r.TaskId.StartsWith(RepairConstants.InfraTaskIdPrefix, StringComparison.OrdinalIgnoreCase)
                    && r.ResultStatus == RepairTaskResult.Cancelled);
                
            Assert.IsTrue(testRepairTasks.Any());
            
            testRepairTasks =
                repairTasks.Where(
                    r => DateTime.UtcNow.Subtract(r.CompletedTimestamp.Value) < TimeSpan.FromMinutes(2) 
                        && RepairTaskEngine.MatchSubstring(RepairTaskEngine.NodeRepairActionSubstrings, r.Action));

            Assert.IsTrue(testRepairTasks.Count() == escalationCount);

            // Clean up.
            foreach (RepairTask repair in testRepairTasks)
            {
                await fabricClient.RepairManager.DeleteRepairTaskAsync(repair.TaskId, repair.Version);
            }
        }

        [TestMethod]
        public async Task MaxExecutionTime_Canceled_Repair()
        {
            string testRulesFilePath = Path.Combine(Environment.CurrentDirectory, "testrules_wellformed_maxexecution.guan");
            string[] rules = await File.ReadAllLinesAsync(testRulesFilePath, token);
            List<string> repairRules = FabricHealerManager.ParseRulesFile(rules);
            var repairData = new TelemetryData
            {
                ApplicationName = "fabric:/test0",
                NodeName = NodeName,
                Metric = "ThreadCount",
                HealthState = HealthState.Warning,
                Code = SupportedErrorCodes.AppErrorTooManyThreads,
                ServiceName = "fabric:/test0/service0",
                Value = 42,
                ReplicaId = default,
                PartitionId = default
            };

            TimeSpan maxExecutionTime = TimeSpan.FromSeconds(2);
            repairData.RepairPolicy = new RepairPolicy
            {
                AppName = repairData.ApplicationName,
                RepairIdPrefix = RepairConstants.FHTaskIdPrefix,
                MaxExecutionTime = maxExecutionTime,
                NodeName = repairData.NodeName,
                Code = repairData.Code,
                HealthState = repairData.HealthState,
                ProcessName = repairData.ProcessName,
                RepairId = "_Node_0_test0_service0_ThreadCount",
                ServiceName = repairData.ServiceName,
                MaxTimePostRepairHealthCheck = TimeSpan.FromSeconds(1)
            };

            var executorData = new RepairExecutorData
            {
                RepairPolicy = repairData.RepairPolicy
            };

            try
            {
                // Don't block.
                _ = TestInitializeGuanAndRunQuery(repairData, repairRules, executorData);
            }
            catch (GuanException)
            {
                throw;
            }

            // Verify that the repair task was Cancelled within max execution time.
            RepairTaskList repairTasks = null;
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool isCanceledWithinMaxExecWindow = false;

            while (stopwatch.Elapsed < TimeSpan.FromSeconds(15))
            {
                repairTasks =
                   await fabricClient.RepairManager.GetRepairTaskListAsync();
                
                Assert.IsNotNull(repairTasks);

                if (repairTasks.Any(
                        r => !string.IsNullOrWhiteSpace(r.ExecutorData) 
                          && JsonSerializationUtility.TrySerializeObject(executorData, out string exdata) 
                          && r.ExecutorData.Equals(exdata)
                          && r.State == RepairTaskState.Completed
                          && r.ResultStatus == RepairTaskResult.Cancelled
                          && r.CompletedTimestamp.Value.Subtract(r.ExecutingTimestamp.Value) <= maxExecutionTime))
                {
                    isCanceledWithinMaxExecWindow = true;
                    break;
                }

                await Task.Delay(1000, token);
            }

            Assert.IsTrue(isCanceledWithinMaxExecWindow);
        }

        /* private Helpers */

        private static async Task TestInitializeGuanAndRunQuery(TelemetryData repairData, List<string> repairRules, RepairExecutorData executorData)
        {
            await RepairTaskManager.RunGuanQueryAsync(repairData, repairRules, token, executorData);
        }

        private static async Task<(bool, TelemetryData data)> 
            IsEntityInWarningStateAsync(string appName = null, string serviceName = null, string nodeName = null)
        {
            EntityHealth healthData = null;

            if (appName != null)
            {
                healthData = await fabricClient.HealthManager.GetApplicationHealthAsync(new Uri(appName));
            }
            else if (serviceName != null)
            {
                healthData = await fabricClient.HealthManager.GetServiceHealthAsync(new Uri(serviceName));
            }
            else if (nodeName != null)
            {
                healthData = await fabricClient.HealthManager.GetNodeHealthAsync(nodeName);
            }
            else
            {
                return (false, null);
            }

            if (healthData == null)
            {
                return (false, null);
            }

            bool isInWarning = healthData.HealthEvents.Any(h => h?.HealthInformation?.HealthState == HealthState.Warning);

            if (!isInWarning)
            {
                return (false, null);
            }

            HealthEvent healthEventWarning = healthData.HealthEvents.FirstOrDefault(h => h.HealthInformation?.HealthState == HealthState.Warning);
            _ = JsonSerializationUtility.TryDeserializeObject(healthEventWarning.HealthInformation.Description, out TelemetryData data);

            return (true, data);
        }

        // FabricHealearProxy tests \\

        // This specifies that you want FabricHealer to repair a service instance deployed to a Fabric node named NodeName.
        // FabricHealer supports both Replica and CodePackage restarts of services. The logic rules will dictate which one of these happens,
        // so make sure to craft a specific logic rule that makes sense for you (and use some logic!).
        // Note that, out of the box, FabricHealer's AppRules.guan file located in the FabricHealer project's PackageRoot/Config/LogicRules folder
        // already has a restart replica catch-all (applies to any service) rule that will restart the primary replica of
        // the specified service below, deployed to the a specified Fabric node. 
        // By default, if you only supply NodeName and ServiceName, then FabricHealerProxy assumes the target EntityType is Service. This is a convience to limit how many facts
        // you must supply in a RepairFacts instance. For any type of repair, NodeName is always required.

        static readonly RepairFacts RepairFactsExistingServiceTarget = new()
        {
            // The service here must be one that is running in your test cluster.
            // TODO: install a local test app as part of tests.
            ServiceName = "fabric:/TestApp42/ChildProcessCreator",
            NodeName = NodeName,
            // Specifying Source is Required for unit tests.
            // For unit tests, there is no FabricRuntime static, so FHProxy, which utilizes this type, will fail unless Source is provided here.
            Source = "fabric:/test"
        };

        static readonly RepairFacts RepairFactsNonExistingServiceTarget = new()
        {
            // The service here must be one that is running in your test cluster.
            ServiceName = "fabric:/test/foo",
            NodeName = NodeName,
            // Specifying Source is Required for unit tests.
            // For unit tests, there is no FabricRuntime static, so FHProxy, which utilizes this type, will fail unless Source is provided here.
            Source = "fabric:/test"
        };

        // This specifies that you want FabricHealer to repair a Fabric node named _Node_0. The only supported Fabric node repair in FabricHealer is a Restart.
        // Related rules can be found in FabricNodeRules.guan file in the FabricHealer project's PackageRoot/Config/LogicRules folder.
        // So, implicitly, this means you want FabricHealer to restart _Node_0. By default, if you only supply NodeName, then FabricHealerProxy assumes the target EntityType is Node.
        static readonly RepairFacts RepairFactsNodeTarget = new()
        {
            NodeName = NodeName,
            // Specifying Source is Required for unit tests.
            // For unit tests, there is no FabricRuntime static, so FHProxy, which utilizes this type, will fail unless Source is provided here.
            Source = "fabric:/Test"
        };

        // Initiate a reboot of the machine hosting the specified Fabric node, _Node_4. This will be executed by the InfrastructureService for the related node type.
        // The related logic rules for this repair target are housed in FabricHealer's MachineRules.guan file.
        static readonly RepairFacts RepairFactsMachineTarget = new()
        {
            NodeName = NodeName,
            EntityType = FabricHealer.EntityType.Machine,
            // Specifying Source is Required for unit tests.
            // For unit tests, there is no FabricRuntime static, so FHProxy, which utilizes this type, will fail unless Source is provided here.
            Source = "fabric:/Test"
        };

        // Restart system service process.
        static readonly RepairFacts SystemServiceRepairFacts = new()
        {
            ApplicationName = "fabric:/System",
            NodeName = NodeName,
            ProcessName = "FabricDCA",
            ProcessId = 73588,
            Code = SupportedErrorCodes.AppWarningMemoryMB,
            // Specifying Source is Required for unit tests.
            // For unit tests, there is no FabricRuntime static, so FHProxy, which utilizes this type, will fail unless Source is provided here.
            Source = "fabric:/Test"
        };

        // Disk - Delete files. This only works if FabricHealer instance is present on the same target node.
        // Note the rules in FabricHealer\PackageRoot\LogicRules\DiskRules.guan file in the FabricHealer project.
        static readonly RepairFacts DiskRepairFacts = new()
        {
            NodeName = NodeName,
            EntityType = FabricHealer.EntityType.Disk,
            Metric = SupportedMetricNames.DiskSpaceUsageMb,
            Code = SupportedErrorCodes.NodeWarningDiskSpaceMB,
            // Specifying Source is Required for unit tests.
            // For unit tests, there is no FabricRuntime static, so FHProxy, which utilizes this type, will fail unless Source is provided here.
            Source = "fabric:/Test"
        };

        // Custom Source and Property, like from a watchdog service that wants FH to schedule a machine repair for a target node.
        static readonly RepairFacts WatchDogMachineRepairFacts = new()
        {
            HealthState = HealthState.Error,
            NodeName = NodeName,
            EntityType = FabricHealer.EntityType.Machine,
            Property = "EventLogError WER 55",
            Source = "EventLogWatchdog007"
        };

        // For use in the IEnumerable<RepairFacts> RepairEntityAsync overload.
        static readonly List<RepairFacts> RepairFactsList = new()
        {
            DiskRepairFacts,
            RepairFactsMachineTarget,
            RepairFactsNodeTarget,
            RepairFactsExistingServiceTarget,
            SystemServiceRepairFacts
        };

        [TestMethod]
        public async Task FHProxy_Service_Facts_Generate_Entity_Health_Warning()
        {
            await FabricHealerProxy.Instance.RepairEntityAsync(RepairFactsExistingServiceTarget, token);

            // FHProxy creates or renames Source with trailing id ("FabricHealerProxy");
            Assert.IsTrue(RepairFactsExistingServiceTarget.Source.EndsWith(FHProxyId));

            var (generatedWarning, data) = await IsEntityInWarningStateAsync(null, RepairFactsExistingServiceTarget.ServiceName);
            Assert.IsTrue(generatedWarning);
            Assert.IsTrue(data != null);
        }

        [TestMethod]
        public async Task FHProxy_Node_Facts_Generates_Entity_Health_Warning()
        {
            // This will put the entity into Warning with a specially-crafted Health Event description (serialized instance of ITelemetryData type).
            await FabricHealerProxy.Instance.RepairEntityAsync(RepairFactsNodeTarget, token);

            // FHProxy creates or renames Source with trailing id ("FabricHealerProxy");
            Assert.IsTrue(RepairFactsNodeTarget.Source.EndsWith(FHProxyId));

            var (generatedWarning, data) = await IsEntityInWarningStateAsync(null, null, NodeName);
            Assert.IsTrue(generatedWarning);
            Assert.IsTrue(data != null);
        }

        [TestMethod]
        public async Task FHProxy_Missing_Fact_Generates_MissingRepairFactsException()
        {
            var repairFacts = new RepairFacts
            {
                ServiceName = "fabric:/foo/bar",
                // Specifying Source is Required for unit tests.
                // For unit tests, there is no FabricRuntime static, so FHProxy, which utilizes this type, will fail unless Source is provided here.
                Source = "fabric:/Test"
            };

            await Assert.ThrowsExceptionAsync<MissingRepairFactsException>(async () =>
            {
                try
                {
                    await FabricHealerProxy.Instance.RepairEntityAsync(repairFacts, token);
                }
                finally
                {
                    // Ensure FHProxy cleans up its health reports.
                    FabricHealerProxy.Instance.Close();
                }
            });
        }

        [TestMethod]
        public async Task FHProxy_NonExistent_ServiceTarget_Generates_ServiceNotFoundException()
        {
            await Assert.ThrowsExceptionAsync<ServiceNotFoundException>(async () => 
            {
                await FabricHealerProxy.Instance.RepairEntityAsync(RepairFactsNonExistingServiceTarget, token);
            });
        }

        [TestMethod]
        public async Task FHProxy_Missing_Fact_Generates_NodeNotFoundException()
        {
            var repairFacts = new RepairFacts
            {
                NodeName = "_Node_007x",
                // No need for Source here as an invalid node will be detected before the Source value matters.
            };

            await Assert.ThrowsExceptionAsync<NodeNotFoundException>(async () => 
            { 
                await FabricHealerProxy.Instance.RepairEntityAsync(repairFacts, token); 
            });
        }

        [TestMethod]
        public async Task FHProxy_Multiple_Entity_Repair_Facts_Generate_Warnings()
        {
            if (!IsLocalSFRuntimePresent())
            {
                throw new InternalTestFailureException("You must run this test with an active local (dev) SF cluster.");
            }

            // This will put the entity into Warning with a specially-crafted Health Event description (serialized instance of ITelemetryData type).
            await FabricHealerProxy.Instance.RepairEntityAsync(RepairFactsList, token);

            foreach (var repair in RepairFactsList)
            {
                if (repair.ServiceName != null)
                {
                    var (generatedWarningService, sdata) = await IsEntityInWarningStateAsync(null, repair.ServiceName);
                    Assert.IsTrue(generatedWarningService);
                    Assert.IsTrue(sdata != null);
                }
                else if (repair.EntityType is FabricHealer.EntityType.Disk or FabricHealer.EntityType.Machine or FabricHealer.EntityType.Node)
                {
                    var (generatedWarningNode, ndata) = await IsEntityInWarningStateAsync(null, null, NodeName);
                    Assert.IsTrue(generatedWarningNode);
                    Assert.IsTrue(ndata != null);
                }

                // FHProxy creates or renames Source with trailing id ("FabricHealerProxy");
                Assert.IsTrue(repair.Source.EndsWith(FHProxyId));
            }
        }

        [TestMethod]
        public void Multi_Type_Guid_Support_Compatibility_Test()
        {
            Guid? nullableGuid = (Guid?)Guid.NewGuid();
            Guid? nullGuid = null;
            string guidString = nullableGuid.ToString();
            string empty = string.Empty;
            string whitespace = "   ";
            string randomChars = ".-$--%->-d-e-c-[}-o";

            Assert.IsFalse(RepairExecutor.TryGetGuid(nullGuid, out _));
            Assert.IsFalse(RepairExecutor.TryGetGuid(whitespace, out _));
            Assert.IsFalse(RepairExecutor.TryGetGuid(empty, out _));
            Assert.IsFalse(RepairExecutor.TryGetGuid(randomChars, out _));

            Assert.IsTrue(RepairExecutor.TryGetGuid(nullableGuid, out _));
            Assert.IsTrue(RepairExecutor.TryGetGuid(guidString, out _));
        }
    }
}