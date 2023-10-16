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
using System.Text;
using TimeoutException = System.TimeoutException;

namespace FHTest
{
    // NOTE: Run these tests on your machine with a local 1 or 5 node SF dev cluster running.
    // RepairManagerService must be running on your local dev cluster. See below for instructions on how to deploy RM to your local dev cluster.
    // These tests are hybrid unit/end-to-end tests. They are not pure unit tests.

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

            // RM must be deployed to local dev cluster to run these tests successfully. These are hybrid unit/end-to-end tests.

            // Steps to deploy RM to local dev cluster:
            // Open the clusterManifest.xml file located on your local dev cluster (e.g., C:\SfDevCluster\Data\ClusterManifest.xml).
            // Copy the contents (this is important to ensure you are using the right manifest for the version of SF you installed).
            // Paste the contents into new file.
            // Then, add this RM Service section to the file under the <FabricSettings> section:
            // For 1 node cluster (e.g., you named your updated file clusterManifestRM.xml):
            //     <Section Name="RepairManager">
            //          <Parameter Name="MinReplicaSetSize" Value="1" />
            //          <Parameter Name="TargetReplicaSetSize" Value="1" />
            //     </Section>
            //
            // For 5 node cluster (e.g., you named your updated file clusterManifestRM_5node.xml):
            //     <Section Name="RepairManager">
            //          <Parameter Name="MinReplicaSetSize" Value="3" />
            //          <Parameter Name="TargetReplicaSetSize" Value="3" />
            //     </Section>
            //
            // Save as clusterManifestRM.xml or clusterManifestRM_5node.xml(depending upon your local node configuration(1 or 5 node cluster)) to some location of your choice.

            // 1. Stop your local cluster.
            // 2. Open a PowerShell window. 
            // 3. Run this cmdlet (for 1 node dev cluster, in this example. For 5 node, you would use clusterManifestRM_5node.xml, per above):
            //    Update-ServiceFabricNodeConfiguration -ClusterManifestPath [path to your updated clusterManifest file] -Force
            // 4. Start your local cluster.
            // 5. Wait for the cluster to come up to green state.
            // 6. Run the tests.
            ServiceList serviceList = await fabricClient.QueryManager.GetServiceListAsync(
                                             new Uri("fabric:/System"),
                                             new Uri("fabric:/System/RepairManagerService"),
                                             TimeSpan.FromSeconds(60),
                                             token);

            if (serviceList == null || serviceList.Count == 0)
            {
                throw new Exception("RepairManagerService must be running to execute these tests correctly.");
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


            FabricHealerManager _ = new (TestServiceContext, token);
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
                        await FabricRepairTasks.CancelRepairTaskAsync(repairTask);
                    }

                    await Task.Delay(1000);
                }
                catch
                {

                }
            }

            await FabricHealerManager.TryClearExistingHealthReportsAsync();
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
                TimeSpan maxAppInstallWaitTime = TimeSpan.FromSeconds(15);
                await Task.Delay(maxAppInstallWaitTime);
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
        
        /* End Helpers */

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
                        await FabricRepairTasks.CancelRepairTaskAsync(repairTask);
                    }

                    await Task.Delay(1000);
                }
                catch
                {

                }
            }

            string path = @"C:\SFDevCluster\Log\QueryTraces";

            try
            {
                string[] files = Directory.GetFiles(path);

                foreach (string file in files)
                {
                    if (file.Contains("foo"))
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (IOException)
            {

            }

            await FabricHealerManager.TryClearExistingHealthReportsAsync();
            fabricClient.Dispose();
        }

        /* GuanLogic Tests 
           The tests below validate entity-specific logic rules and the successful scheduling of related local repair jobs. */


        [TestMethod]
        public async Task AppRules_Repair_Successful_Validate_RuleTracing()
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

            // Validate that the repair predicate is traced.
            try
            {
                var nodeHealth =
                    await fabricClient.HealthManager.GetNodeHealthAsync(NodeName, TimeSpan.FromSeconds(60), CancellationToken.None);
                var FHNodeEvents = nodeHealth.HealthEvents?.Where(
                        s => s.HealthInformation.SourceId.Contains("AppRules.guan")
                        && s.HealthInformation.Description.Contains("RestartCodePackage"));

                Assert.IsTrue(FHNodeEvents.Any());
            }
            catch (Exception e) when (e is ArgumentException or FabricException or TimeoutException)
            {
                throw;
            }
        }

        [TestMethod]
        public async Task MachineRules_EnsureWellFormedRules_QueryInitialized_Successful()
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
            catch (GuanException)
            {
                throw;
            }

            // Based on repair rules in MachineRules.guan file used for this test, a repair should NOT be scheduled based on the above facts.
            var repairs = await fabricClient.RepairManager.GetRepairTaskListAsync(RepairConstants.InfraTaskIdPrefix, RepairTaskStateFilter.Active, null);
            Assert.IsFalse(repairs.Any());

            repairData = new TelemetryData
            {
                EntityType = EntityType.Machine,
                NodeName = NodeName,
                HealthState = HealthState.Error,
                Source = "Foo",
                Property = "Bar"
            };

            repairData.RepairPolicy = new RepairPolicy
            {
                RepairId = $"Test42_MachineRepair{NodeName}_Scope",
                RepairIdPrefix = RepairConstants.InfraTaskIdPrefix,
                NodeName = repairData.NodeName,
                HealthState = repairData.HealthState
            };

            executorData.RepairPolicy = repairData.RepairPolicy;

            try
            {
                await TestInitializeGuanAndRunQuery(repairData, repairRules, executorData);
            }
            catch (GuanException)
            {
                throw;
            }

            Assert.IsFalse(repairs.Any());
        }

        [TestMethod]
        public async Task DiskRules_DiskSpace_Percentage_Repair_Successful_Validate_RuleTracing()
        {
            // Create temp files.
            // You can use whatever path you want, but you need to make sure that is also specified in the related test logic rule (service-fabric-healer\FHTest\PackageRoot\Config\LogicRules\DiskRules.guan).
            byte[] bytes = Encoding.ASCII.GetBytes("foo bar baz foo bar baz foo bar baz foo bar baz foo bar baz foo bar baz foo bar baz");
            string path = @"C:\FHTest\cluster_observer_logs";

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            // Create two 2GB files in target directory (path).
            for (int i = 0; i < 2; i++)
            {
                using var f = File.Create(Path.Combine(path, $"foo{i}.txt"), 500000, FileOptions.WriteThrough);

                for (int j = 0; j < 25000000; ++j)
                {
                    f.Write(bytes);
                }
            }

            var repairData = new TelemetryData
            {
                EntityType = EntityType.Disk,
                NodeName = NodeName,
                Metric = SupportedMetricNames.DiskSpaceUsagePercentage,
                Code = SupportedErrorCodes.NodeErrorDiskSpacePercent,
                HealthState = HealthState.Warning,
                Source = $"DiskObserver({SupportedErrorCodes.NodeErrorDiskSpacePercent})",
                Property = $"{NodeName}_{SupportedMetricNames.DiskSpaceUsagePercentage.Replace(" ", string.Empty)}",
                Value = 90.0
            };

            Assert.IsTrue(JsonSerializationUtility.TrySerializeObject(repairData, out string description));

            Logger logger = new("FHTest", Environment.CurrentDirectory);
            FabricHealthReporter healthReporter = new(logger);

            FabricHealer.Utilities.HealthReport healthReport = new()
            {
                Code = repairData.Code,
                EntityType = EntityType.Disk,
                HealthMessage = description,
                HealthReportTimeToLive = TimeSpan.FromMinutes(5),
                State = HealthState.Warning,
                NodeName = NodeName,
                Property = repairData.Property,
                SourceId = RepairConstants.FabricHealer,
                HealthData = repairData,
                ResourceUsageDataProperty = SupportedMetricNames.DiskSpaceUsagePercentage
            };

            using FabricHealerManager _ = new (TestServiceContext, token);
            healthReporter.ReportHealthToServiceFabric(healthReport);

            await FabricHealerManager.InitializeAsync();
            await FabricHealerManager.ProcessHealthEventsAsync();
            
            // Validate that the repair rule is traced and repair predicate succeeds.
            try
            {
                var nodeHealth =
                    await fabricClient.HealthManager.GetNodeHealthAsync(NodeName, TimeSpan.FromSeconds(60), CancellationToken.None);
                var FHNodeEvents = nodeHealth.HealthEvents?.Where(
                        s => s.HealthInformation.SourceId.Contains("DiskRules.guan") && s.HealthInformation.Description.Contains("DeleteFiles"));

                Assert.IsTrue(FHNodeEvents.Any());
                Assert.IsTrue(FHNodeEvents.Any(e => e.HealthInformation.Description.Contains("cluster_observer_logs")));
            }
            catch (Exception e) when (e is ArgumentException or FabricException or TimeoutException)
            {
                throw;
            }

            Assert.IsFalse(Directory.GetFiles(path).Any(f => f == "foo0.txt" || f == "foo1.txt"));
        }

        [TestMethod]
        public async Task DiskRules_FolderSizeMB_Repair_Successful_Validate_RuleTracing()
        {
            // Create temp files. This assumes C:\SFDevCluster\Log\QueryTraces exists. This is the path used in the related logic rule.
            // See DiskRules.guan in FHTest\PackageRoot\Config\LogicRules folder.
            // You can use whatever path you want, but you need to make sure that is also specified in the related test logic rule.
            byte[] bytes = Encoding.ASCII.GetBytes("foo bar baz foo bar baz foo bar baz foo bar baz foo bar baz foo bar baz foo bar baz");
            string path = @"C:\SFDevCluster\Log\QueryTraces";

            for (int i = 0; i < 5; i++)
            {
                using var f = File.Create(Path.Combine(path, $"foo{i}.txt"), 500, FileOptions.WriteThrough);

                for (int j = 0; j < 50000; ++j)
                {
                    f.Write(bytes);
                }
            }

            // This will be the data used to create a repair task.
            var repairData = new TelemetryData
            {
                EntityType = EntityType.Disk,
                NodeName = NodeName,
                Metric = SupportedMetricNames.FolderSizeMB,
                Code = SupportedErrorCodes.NodeWarningFolderSizeMB,
                HealthState = HealthState.Warning,
                Source = $"DiskObserver({SupportedErrorCodes.NodeWarningFolderSizeMB})",
                Property = $"{NodeName}_{SupportedMetricNames.FolderSizeMB.Replace(" ", string.Empty)}",
                Value = 5
            };

            Assert.IsTrue(JsonSerializationUtility.TrySerializeObject(repairData, out string description));

            Logger logger = new("FHTest", Environment.CurrentDirectory);
            FabricHealthReporter healthReporter = new(logger);

            FabricHealer.Utilities.HealthReport healthReport = new()
            {
                Code = repairData.Code,
                EntityType = EntityType.Disk,
                HealthMessage = description,
                HealthReportTimeToLive = TimeSpan.FromMinutes(5),
                State = HealthState.Warning,
                NodeName = NodeName,
                Property = repairData.Property,
                SourceId = RepairConstants.FabricHealer,
                HealthData = repairData,
                ResourceUsageDataProperty = SupportedMetricNames.FolderSizeMB
            };

            using FabricHealerManager _ = new (TestServiceContext, token);
            healthReporter.ReportHealthToServiceFabric(healthReport);

            await FabricHealerManager.InitializeAsync();
           await FabricHealerManager.ProcessHealthEventsAsync();
          
            // Validate that both the repair rule (contains LogRule predicate) and repair predicate (w/expanded variables) is traced.
            try
            {
                var nodeHealth =
                    await fabricClient.HealthManager.GetNodeHealthAsync(NodeName, TimeSpan.FromSeconds(60), CancellationToken.None);
                var FHNodeEvents = nodeHealth.HealthEvents?.Where(
                        s => s.HealthInformation.SourceId.Contains("DiskRules.guan") && s.HealthInformation.Description.Contains("DeleteFiles"));

                Assert.IsTrue(FHNodeEvents.Any());
                Assert.IsTrue(FHNodeEvents.Any(e => e.HealthInformation.Description.Contains("QueryTraces")));
            }
            catch (Exception e) when (e is ArgumentException or FabricException or TimeoutException)
            {
                throw;
            }

            Assert.IsFalse(Directory.GetFiles(path).Any(f => f.StartsWith("foo") && f.EndsWith(".txt")));
        }

        [TestMethod]
        public async Task DiskRules_FolderSizeMB_Repair_Successful_Validate_RuleTracing_MaxExecutionTime()
        {
            // Create temp files. This assumes C:\SFDevCluster\Log\QueryTraces exists. This is the path used in the related logic rule.
            // See DiskRules.guan in FHTest\PackageRoot\Config\LogicRules folder.
            // You can use whatever path you want, but you need to make sure that is also specified in the related test logic rule.
            byte[] bytes = Encoding.ASCII.GetBytes("foo bar baz foo bar baz foo bar baz foo bar baz foo bar baz foo bar baz foo bar baz");
            string path = @"C:\SFDevCluster\Log\QueryTraces";

            for (int i = 0; i < 5; i++)
            {
                using var f = File.Create(Path.Combine(path, $"foo{i}.txt"), 500, FileOptions.WriteThrough);

                for (int j = 0; j < 50000; ++j)
                {
                    f.Write(bytes);
                }
            }

            // This will be the data used to create a repair task.
            var repairData = new TelemetryData
            {
                EntityType = EntityType.Disk,
                NodeName = NodeName,
                Metric = SupportedMetricNames.FolderSizeMB,
                Code = SupportedErrorCodes.NodeWarningFolderSizeMB,
                HealthState = HealthState.Warning,
                Source = $"DiskObserver({SupportedErrorCodes.NodeWarningFolderSizeMB})_FHTest_Ex",
                Property = $"{NodeName}_{SupportedMetricNames.FolderSizeMB.Replace(" ", string.Empty)}",
                Value = 5
            };

            Assert.IsTrue(JsonSerializationUtility.TrySerializeObject(repairData, out string description));

            Logger logger = new("FHTest", Environment.CurrentDirectory);
            FabricHealthReporter healthReporter = new(logger);

            FabricHealer.Utilities.HealthReport healthReport = new()
            {
                Code = repairData.Code,
                EntityType = EntityType.Disk,
                HealthMessage = description,
                HealthReportTimeToLive = TimeSpan.FromMinutes(5),
                State = HealthState.Warning,
                NodeName = NodeName,
                Property = repairData.Property,
                SourceId = RepairConstants.FabricHealer,
                HealthData = repairData,
                ResourceUsageDataProperty = SupportedMetricNames.FolderSizeMB
            };

            using FabricHealerManager _ = new (TestServiceContext, token);
            healthReporter.ReportHealthToServiceFabric(healthReport);

            await FabricHealerManager.InitializeAsync();
            await FabricHealerManager.ProcessHealthEventsAsync();
            
            // Validate that both the repair rule (contains LogRule predicate) and repair predicate (w/expanded variables) is traced.
            try
            {
                var nodeHealth =
                    await fabricClient.HealthManager.GetNodeHealthAsync(NodeName, TimeSpan.FromSeconds(60), CancellationToken.None);
                var FHNodeEvents = nodeHealth.HealthEvents?.Where(
                        s => s.HealthInformation.SourceId.Contains("DiskRules.guan") && s.HealthInformation.Description.Contains("DeleteFiles"));

                Assert.IsTrue(FHNodeEvents.Any());
                Assert.IsTrue(FHNodeEvents.Any(e => e.HealthInformation.Description.Contains("QueryTraces")));
            }
            catch (Exception e) when (e is ArgumentException or FabricException or TimeoutException)
            {
                throw;
            }
            
            await Task.Delay(TimeSpan.FromSeconds(5));

            // Ensure that the repair task was cancelled per MaxExecutionTime.
            var repairs = await fabricClient.RepairManager.GetRepairTaskListAsync(RepairConstants.FHTaskIdPrefix, RepairTaskStateFilter.Completed, null);   
            Assert.IsTrue(repairs.Any(
                    r => JsonSerializationUtility.TryDeserializeObject(
                      r.ExecutorData, out RepairExecutorData exData)
                        && exData.RepairPolicy.MaxExecutionTime <= TimeSpan.FromSeconds(1) 
                        && r.ResultStatus == RepairTaskResult.Cancelled));
            
            // The execution should have been cancelled.
            Assert.IsTrue(Directory.GetFiles(path).Count(f => f.Contains("foo") && f.EndsWith(".txt")) == 5);
        }

        [TestMethod]
        public async Task FabricNodeRules_EnsureWellFormedRules_QueryInitialized_Successful()
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

                    await FabricRepairTasks.CancelRepairTaskAsync(repairTasks.First(r => r.Action == "RestartFabricNode"));
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
        public async Task XReplicaRules_MemoryMB_Repair_Successful_Validate_Rule_Tracing()
        {
            var partitions = await fabricClient.QueryManager.GetPartitionListAsync(new Uri("fabric:/TestApp42/ChildProcessCreator"));
            
            // This service is as stateless, -1, singleton partition.
            Guid partition = partitions[0].PartitionInformation.Id;
            var replicas = await fabricClient.QueryManager.GetReplicaListAsync(partition);
            Replica replica = replicas.First(r => r.ReplicaStatus == ServiceReplicaStatus.Ready);
            long replicaId = replica.Id;

            // This will be the data used to create a repair task.
            var repairData = new TelemetryData
            {
                ApplicationName = "fabric:/TestApp42",
                EntityType = EntityType.Service,
                NodeName = NodeName,
                Code = SupportedErrorCodes.AppErrorActiveEphemeralPortsPercent,
                HealthState = HealthState.Warning,
                PartitionId = partition.ToString(),
                ReplicaId = replicaId,
                Source = $"AppObserver({SupportedErrorCodes.AppErrorActiveEphemeralPortsPercent})",
                Property = "TestApp42_ChildProcessCreator_Ports",
                ProcessName = "ChildProcessCreator",
                ServiceName = "fabric:/TestApp42/ChildProcessCreator",
                Value = 80
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

            await Task.Delay(TimeSpan.FromSeconds(5));

            // Validate that the repair predicate is traced.
            try
            {
                NodeHealth nodeHealth =
                    await fabricClient.HealthManager.GetNodeHealthAsync(NodeName, TimeSpan.FromSeconds(60), CancellationToken.None);
                
                IEnumerable<HealthEvent> FHNodeEvents = nodeHealth.HealthEvents?.Where(
                            s => s.HealthInformation.SourceId.Contains("ReplicaRules.guan")
                              && s.HealthInformation.Description.Contains("RestartReplica"));

                Assert.IsTrue(FHNodeEvents.Any());
            }
            catch (Exception e) when (e is ArgumentException or FabricException or TimeoutException)
            {
                throw;
            }
        }

        [TestMethod]
        public async Task XSystemServiceRules_MemoryMb_Repair_Successful_Validate_RuleTracing()
        {
            var repairData = new TelemetryData
            {
                ApplicationName = "fabric:/System",
                Code = SupportedErrorCodes.AppErrorMemoryMB,
                EntityType = EntityType.Process,
                NodeName = NodeName,
                HealthState = HealthState.Warning,
                ProcessName = "FabricDCA",
                ProcessId = Process.GetProcessesByName("FabricDCA")[0].Id,
                Source = "FabricHealer_Test_SystemServiceRules",
                Property = "MemoryInMb_FabricDCA",
                Value = 1024
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

            // Validate that the repair predicate is traced.
            try
            {
                var nodeHealth =
                    await fabricClient.HealthManager.GetNodeHealthAsync(NodeName, TimeSpan.FromSeconds(60), CancellationToken.None);
                var FHNodeEvents = nodeHealth.HealthEvents?.Where(
                        s => s.HealthInformation.SourceId.Contains(FabricHealerManager.CurrentlyExecutingLogicRulesFileName)
                        && s.HealthInformation.Description.Contains("TimeScopedRestartFabricSystemProcess"));

                Assert.IsTrue(FHNodeEvents.Any());
            }
            catch (Exception e) when (e is ArgumentException or FabricException or TimeoutException)
            {
                throw;
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
                await FabricRepairTasks.CancelRepairTaskAsync(repairTasks.First());
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
            using FabricHealerManager _ = new(TestServiceContext, token);
            int escalationCount = 4; // reboot, reimage, heal, triage.
            RepairTaskList repairTasks = null;

            for (int i = 0; i < escalationCount; i++)
            {
                await FabricHealerProxy.Instance.RepairEntityAsync(WatchDogMachineRepairFacts, token);
                await FabricHealerManager.ProcessHealthEventsAsync();

                // Allow time for repair job creation.
                await Task.Delay(5000, token);

                repairTasks = await fabricClient.RepairManager.GetRepairTaskListAsync(
                                      RepairConstants.InfraTaskIdPrefix, RepairTaskStateFilter.Active, null);

                if (!repairTasks.Any())
                {
                    await FabricHealerProxy.Instance.RepairEntityAsync(WatchDogMachineRepairFacts, token);
                    await FabricHealerManager.ProcessHealthEventsAsync();
                    await Task.Delay(5000, token);
                    repairTasks = await fabricClient.RepairManager.GetRepairTaskListAsync(
                                     RepairConstants.InfraTaskIdPrefix, RepairTaskStateFilter.Active, null);
                }

                Assert.IsTrue(repairTasks.Any());

                await FabricRepairTasks.CancelRepairTaskAsync(repairTasks.First());
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

        /* private Helpers */

        private static async Task TestInitializeGuanAndRunQuery(TelemetryData repairData, List<string> repairRules, RepairExecutorData executorData)
        {
            await RepairTaskManager.RunGuanQueryAsync(repairData, repairRules, CancellationToken.None, executorData);
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
            // Adding extra spaces to ensure FHProxy's URI string fixer works.
            ServiceName = "fabric: /TestApp42 / ChildProcessCreator",
            NodeName = NodeName,
            // Specifying Source is Required for unit tests.
            // For unit tests, there is no FabricRuntime static, so FHProxy, which utilizes this type, will fail unless Source is provided here.
            Source = "FHTest"
        };

        static readonly RepairFacts RepairFactsNonExistingServiceTarget = new()
        {
            // The service here must be one that is running in your test cluster.
            ServiceName = "fabric:/test/foo",
            NodeName = NodeName,
            // Specifying Source is Required for unit tests.
            // For unit tests, there is no FabricRuntime static, so FHProxy, which utilizes this type, will fail unless Source is provided here.
            Source = "FHTest"
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

        // Policy enablement tests. These validate that when a policy is disabled, no processing will take place when
        // related entities are detected be in Error or Warning health states.
        // Note: All repair policies are enabled in FHTest's Setting.xml, which is part of the ServiceContext creation used,
        // by all tests.

        [TestMethod]
        public async Task Validate_app_repair_disabled_no_processing_warning_state()
        {
            // Clean up existing FH health reports..
            await FabricHealerManager.TryClearExistingHealthReportsAsync();

            FabricHealerManager.ConfigSettings.EnableSystemAppRepair = true;
            FabricHealerManager.ConfigSettings.EnableAppRepair = false;

            var partitions =
                await fabricClient.QueryManager.GetPartitionListAsync(new Uri("fabric:/TestApp42/ChildProcessCreator"));
            Guid partition = partitions[0].PartitionInformation.Id;

            // This will be the data used to create a repair task.
            var repairData = new TelemetryData
            {
                ApplicationName = "fabric:/TestApp42",
                EntityType = EntityType.Service,
                NodeName = NodeName,
                Code = SupportedErrorCodes.AppWarningKvsLvidsPercentUsed,
                HealthState = HealthState.Warning,
                ObserverName = "AppObserver",
                PartitionId = partition.ToString(),
                Source = $"AppObserver({SupportedErrorCodes.AppWarningKvsLvidsPercentUsed})",
                Property = "TestApp42_ChildProcessCreator_Lvids",
                ProcessName = "ChildProcessCreator",
                ServiceName = "fabric:/TestApp42/ChildProcessCreator",
                Value = 90.42
            };

            Assert.IsTrue(JsonSerializationUtility.TrySerializeObject(repairData, out string data));

            FabricHealer.Utilities.HealthReport healthReport = new()
            {
                AppName = new Uri("fabric:/TestApp42"),
                ServiceName = new Uri("fabric:/TestApp42/ChildProcessCreator"),
                NodeName = NodeName,
                State = repairData.HealthState,
                SourceId = repairData.Source,
                Property = repairData.Property,
                EntityType = repairData.EntityType,
                HealthMessage = data,
                Observer = repairData.ObserverName,
                Code = repairData.Code,
                HealthReportTimeToLive = TimeSpan.FromSeconds(90),
            };

            FabricHealthReporter healthReporter = new(new Logger("FHTest"));
            healthReporter.ReportHealthToServiceFabric(healthReport);

            await FabricHealerManager.ProcessHealthEventsAsync();

            // Check to make sure that FH did not process the related health event. \\

            var nodeHealth = await fabricClient.HealthManager.GetNodeHealthAsync(NodeName);
            Assert.IsNotNull(nodeHealth);
            var nodeHealthEvents =
                nodeHealth.HealthEvents.Where(
                    h => h.HealthInformation.Description.Contains(SupportedErrorCodes.AppWarningKvsLvidsPercentUsed)
                    && h.HealthInformation.SourceId.StartsWith(RepairConstants.FabricHealer));

            Assert.IsFalse(nodeHealthEvents.Any());

            // Cleanup.
            healthReport.State = HealthState.Ok;
            healthReporter.ReportHealthToServiceFabric(healthReport);
        }

        [TestMethod]
        public async Task Validate_disk_repair_disabled_no_processing_warning_state()
        {
            // Clean up existing FH health reports..
            await FabricHealerManager.TryClearExistingHealthReportsAsync();

            FabricHealerManager.ConfigSettings.EnableMachineRepair = true;
            FabricHealerManager.ConfigSettings.EnableFabricNodeRepair = true;
            FabricHealerManager.ConfigSettings.EnableDiskRepair = false;

            // This will be the data used to create a repair task.
            var repairData = new TelemetryData
            {
                EntityType = EntityType.Disk,
                NodeName = NodeName,
                Code = SupportedErrorCodes.NodeWarningDiskAverageQueueLength,
                HealthState = HealthState.Warning,
                ObserverName = "DiskObserver",
                Source = $"DiskObserver({SupportedErrorCodes.NodeWarningDiskAverageQueueLength})",
                Property = "C:",
                Value = 1024.0
            };

            Assert.IsTrue(JsonSerializationUtility.TrySerializeObject(repairData, out string data));

            FabricHealer.Utilities.HealthReport healthReport = new()
            {
                NodeName = NodeName,
                State = repairData.HealthState,
                SourceId = repairData.Source,
                Property = repairData.Property,
                EntityType = repairData.EntityType,
                HealthMessage = data,
                Observer = repairData.ObserverName,
                Code = repairData.Code,
                HealthReportTimeToLive = TimeSpan.FromSeconds(90),
            };

            FabricHealthReporter healthReporter = new(new Logger("FHTest"));
            healthReporter.ReportHealthToServiceFabric(healthReport);

            await FabricHealerManager.ProcessHealthEventsAsync();

            // Check to make sure that FH did not process the related health event. \\

            var nodeHealth = await fabricClient.HealthManager.GetNodeHealthAsync(NodeName);
            Assert.IsNotNull(nodeHealth);
            var nodeHealthEvents =
                nodeHealth.HealthEvents.Where(
                    h => h.HealthInformation.Description.Contains(SupportedErrorCodes.NodeWarningDiskAverageQueueLength)
                      && h.HealthInformation.SourceId.StartsWith(RepairConstants.FabricHealer));
            
            Assert.IsFalse(nodeHealthEvents.Any());

            // Cleanup.
            healthReport.State = HealthState.Ok;
            healthReporter.ReportHealthToServiceFabric(healthReport);
        }

        [TestMethod]
        public async Task Validate_machine_repair_disabled_no_processing_warning_state()
        {
            // Clean up existing FH health reports..
            await FabricHealerManager.TryClearExistingHealthReportsAsync();

            FabricHealerManager.ConfigSettings.EnableDiskRepair = true;
            FabricHealerManager.ConfigSettings.EnableFabricNodeRepair = true;
            FabricHealerManager.ConfigSettings.EnableMachineRepair = false;

            // This will be the data used to create a repair task.
            var repairData = new TelemetryData
            {
                EntityType = EntityType.Machine,
                NodeName = NodeName,
                Code = SupportedErrorCodes.NodeWarningMemoryPercent,
                HealthState = HealthState.Warning,
                ObserverName = "NodeObserver",
                Source = $"NodeObserver({SupportedErrorCodes.NodeWarningMemoryPercent})",
                Value = 90.42
            };

            Assert.IsTrue(JsonSerializationUtility.TrySerializeObject(repairData, out string data));

            FabricHealer.Utilities.HealthReport healthReport = new()
            {
                NodeName = NodeName,
                State = repairData.HealthState,
                SourceId = repairData.Source,
                Property = repairData.Property,
                EntityType = repairData.EntityType,
                HealthMessage = data,
                Observer = repairData.ObserverName,
                Code = repairData.Code,
                HealthReportTimeToLive = TimeSpan.FromSeconds(90),
            };

            FabricHealthReporter healthReporter = new(new Logger("FHTest"));
            healthReporter.ReportHealthToServiceFabric(healthReport);

            await FabricHealerManager.ProcessHealthEventsAsync();

            // Check to make sure that FH did not process the related health event. \\

            var nodeHealth = await fabricClient.HealthManager.GetNodeHealthAsync(NodeName);
            Assert.IsNotNull(nodeHealth);
            var nodeHealthEvents =
                nodeHealth.HealthEvents.Where(
                    h => h.HealthInformation.Description.Contains(SupportedErrorCodes.NodeWarningMemoryPercent)
                      && h.HealthInformation.SourceId.StartsWith(RepairConstants.FabricHealer));
            
            Assert.IsFalse(nodeHealthEvents.Any());

            // Cleanup.
            healthReport.State = HealthState.Ok;
            healthReporter.ReportHealthToServiceFabric(healthReport);
            
        }

        [TestMethod]
        public async Task Validate_node_repair_disabled_no_processing_warning_state()
        {
            // Clean up existing FH health reports..
            await FabricHealerManager.TryClearExistingHealthReportsAsync();

            FabricHealerManager.ConfigSettings.EnableDiskRepair = true;
            FabricHealerManager.ConfigSettings.EnableMachineRepair = true;
            FabricHealerManager.ConfigSettings.EnableFabricNodeRepair = false;

            // This will be the data used to create a repair task.
            var repairData = new TelemetryData
            {
                EntityType = EntityType.Node,
                NodeName = NodeName,
                HealthState = HealthState.Error,
                ObserverName = "NodeObserver",
                Source = "NodeObserver",
                Property = $"{NodeName} is in Error...",
                Value = 0
            };

            Assert.IsTrue(JsonSerializationUtility.TrySerializeObject(repairData, out string data));

            FabricHealer.Utilities.HealthReport healthReport = new()
            {
                NodeName = NodeName,
                State = repairData.HealthState,
                SourceId = repairData.Source,
                Property = repairData.Property,
                EntityType = repairData.EntityType,
                HealthMessage = data,
                Observer = repairData.ObserverName,
                HealthReportTimeToLive = TimeSpan.FromSeconds(90),
            };

            FabricHealthReporter healthReporter = new(new Logger("FHTest"));
            healthReporter.ReportHealthToServiceFabric(healthReport);

            await FabricHealerManager.ProcessHealthEventsAsync();

            // Check to make sure that FH did not process the related health event. \\

            var nodeHealth = await fabricClient.HealthManager.GetNodeHealthAsync(NodeName);
            Assert.IsNotNull(nodeHealth);
            var nodeHealthEvents =
                nodeHealth.HealthEvents.Where(
                    h => h.HealthInformation.Description.Contains($"{NodeName} is in Error...")
                      && h.HealthInformation.SourceId.StartsWith(RepairConstants.FabricHealer));
            
            Assert.IsFalse(nodeHealthEvents.Any());

            // Cleanup.
            healthReport.State = HealthState.Ok;
            healthReporter.ReportHealthToServiceFabric(healthReport);
        }

        [TestMethod]
        public async Task Validate_replica_repair_disabled_no_processing_warning_state()
        {
            // Clean up existing FH health reports..
            await FabricHealerManager.TryClearExistingHealthReportsAsync();
            
            FabricHealerManager.ConfigSettings.EnableAppRepair = true;
            FabricHealerManager.ConfigSettings.EnableReplicaRepair = false;

            var partitions =
                await fabricClient.QueryManager.GetPartitionListAsync(new Uri("fabric:/TestApp42/ChildProcessCreator"));
            Guid partition = partitions[0].PartitionInformation.Id;

            var replicaList = await fabricClient.QueryManager.GetReplicaListAsync(partitionId: partition);
            long replicaId = replicaList[0].Id;

            // This will be the data used to create a repair task.
            var repairData = new TelemetryData
            {
                ApplicationName = "fabric:/TestApp42",
                EntityType = EntityType.Partition,
                NodeName = NodeName,
                Code = SupportedErrorCodes.AppWarningKvsLvidsPercentUsed,
                Description = "Partition is unhealthy",
                HealthState = HealthState.Warning,
                ObserverName = "AppObserver",
                PartitionId = partition.ToString(),
                Source = $"AppObserver({SupportedErrorCodes.AppWarningKvsLvidsPercentUsed})",
                Property = "TestApp42_ChildProcessCreator_MemoryMB",
                ReplicaId = replicaId,
                ProcessName = "ChildProcessCreator",
                ServiceName = "fabric:/TestApp42/ChildProcessCreator",
                Value = 1024.0
            };

            Assert.IsTrue(JsonSerializationUtility.TrySerializeObject(repairData, out string data));

            FabricHealer.Utilities.HealthReport healthReport = new()
            {
                AppName = new Uri("fabric:/TestApp42"),
                ServiceName = new Uri("fabric:/TestApp42/ChildProcessCreator"),
                NodeName = NodeName,
                PartitionId = Guid.Parse(repairData.PartitionId),
                State = repairData.HealthState,
                SourceId = repairData.Source,
                Property = repairData.Property,
                EntityType = repairData.EntityType,
                HealthMessage = data,
                Observer = repairData.ObserverName,
                Code = repairData.Code,
                HealthReportTimeToLive = TimeSpan.FromSeconds(90),
            };

            FabricHealthReporter healthReporter = new(new Logger("FHTest"));
            healthReporter.ReportHealthToServiceFabric(healthReport);

            await FabricHealerManager.ProcessHealthEventsAsync();

            // Check to make sure that FH did not process the related health event. \\

            var nodeHealth = await fabricClient.HealthManager.GetNodeHealthAsync(NodeName);
            Assert.IsNotNull(nodeHealth);
            var nodeHealthEvents =
                nodeHealth.HealthEvents.Where(
                    h => h.HealthInformation.Description.Contains(SupportedErrorCodes.AppWarningKvsLvidsPercentUsed)
                      && h.HealthInformation.SourceId.StartsWith(RepairConstants.FabricHealer));

            Assert.IsFalse(nodeHealthEvents.Any());

            // Cleanup.
            healthReport.State = HealthState.Ok;
            healthReporter.ReportHealthToServiceFabric(healthReport);
        }

        [TestMethod]
        public async Task Validate_system_app_repair_disabled_no_processing_warning_state()
        {
            // Clean up existing FH health reports..
            await FabricHealerManager.TryClearExistingHealthReportsAsync();

            FabricHealerManager.ConfigSettings.EnableAppRepair = true;
            FabricHealerManager.ConfigSettings.EnableSystemAppRepair = false;

            // This will be the data used to create a repair task.
            var repairData = new TelemetryData
            {
                ApplicationName = "fabric:/System",
                EntityType = EntityType.Application,
                NodeName = NodeName,
                Code = SupportedErrorCodes.AppErrorTooManyOpenHandles,
                HealthState = HealthState.Warning,
                ObserverName = "FabricSystemObserver",
                Source = $"AppObserver({SupportedErrorCodes.AppErrorTooManyOpenHandles})",
                Property = "FabricDCA_Handles",
                ProcessName = "FabricDCA",
                Value = 10024.0
            };

            Assert.IsTrue(JsonSerializationUtility.TrySerializeObject(repairData, out string data));

            FabricHealer.Utilities.HealthReport healthReport = new()
            {
                AppName = new Uri("fabric:/System"),
                NodeName = NodeName,
                State = repairData.HealthState,
                SourceId = repairData.Source,
                Property = repairData.Property,
                EntityType = repairData.EntityType,
                HealthMessage = data,
                Observer = repairData.ObserverName,
                Code = repairData.Code,
                HealthReportTimeToLive = TimeSpan.FromSeconds(90),
            };

            FabricHealthReporter healthReporter = new(new Logger("FHTest"));
            healthReporter.ReportHealthToServiceFabric(healthReport);

            await FabricHealerManager.ProcessHealthEventsAsync();

            // Check to make sure that FH did not process the related health event. \\

            var nodeHealth = await fabricClient.HealthManager.GetNodeHealthAsync(NodeName);
            Assert.IsNotNull(nodeHealth);
            var nodeHealthEvents =
                nodeHealth.HealthEvents.Where(
                    h => h.HealthInformation.Description.Contains(SupportedErrorCodes.AppErrorTooManyOpenHandles)
                      && h.HealthInformation.SourceId.StartsWith(RepairConstants.FabricHealer));

            Assert.IsFalse(nodeHealthEvents.Any());

            // Cleanup.
            healthReport.State = HealthState.Ok;
            healthReporter.ReportHealthToServiceFabric(healthReport);
        }
    }
}
