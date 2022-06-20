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
using FabricHealer.Repair.Guan;
using System.IO;
using System.Linq;
using FabricHealer.Utilities.Telemetry;
using FabricHealer.Utilities;
using FabricHealer;
using System.Fabric.Repair;
using System.Diagnostics;
using System.Fabric.Health;
using SupportedErrorCodes = FabricHealer.Utilities.SupportedErrorCodes;
using FabricHealer.Interfaces;

namespace FHTest
{
    /// <summary>
    /// NOTE: Run these tests on your machine with a local SF dev cluster running.
    /// TODO: More code coverage.
    /// </summary>

    [TestClass]
    public class FHUnitTests
    {
        private static readonly Uri ServiceName = new Uri("fabric:/app/service");
        private static readonly FabricClient fabricClient = new FabricClient();
        private static readonly ICodePackageActivationContext CodePackageContext
           = new MockCodePackageActivationContext(
                               ServiceName.AbsoluteUri,
                               "applicationType",
                               "Code",
                               "1.0.0.0",
                               Guid.NewGuid().ToString(),
                               @"C:\Log",
                               @"C:\Temp",
                               @"C:\Work",
                               "ServiceManifest",
                               "1.0.0.0");

        private readonly StatelessServiceContext context
                = new StatelessServiceContext(
                                new NodeContext("Node0", new NodeId(0, 1), 0, "NodeType1", "TEST.MACHINE"),
                                CodePackageContext,
                                "FabricHealer.FabricHealerType",
                                ServiceName,
                                null,
                                Guid.NewGuid(),
                                long.MaxValue);

        private readonly CancellationToken token = new CancellationToken();

        // This is the name of the node used on your local dev machine's SF cluster. If you customize this, then change it.
        private const string NodeName = "_Node_0";
        private const string FHProxyId = "FabricHealerProxy";

        // Set this to the full path to your Rules directory in the FabricHealer project's PackageRoot\Config directory.
        // e.g., if developing on Windows, then something like @"C:\Users\[me]\source\repos\service-fabric-healer\FabricHealer\PackageRoot\Config\LogicRules\";
        private const string FHRulesDirectory = @"C:\Users\ctorre\source\repos\service-fabric-healer\FabricHealer\PackageRoot\Config\LogicRules\";

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

        /// <summary>
        /// This function cancels the local repair tasks created by the tests.
        /// </summary>
        /// <returns></returns>
        private static async Task CleanupTestRepairJobsAsync()
        {
            // Complete (Cancel) any existing Test Repair Jobs.
            try
            {
                var repairTasks = await fabricClient.RepairManager.GetRepairTaskListAsync();
                var testRepairTasks = repairTasks.Where(r => r.TaskId.EndsWith("TEST_0"));

                foreach (var repairTask in testRepairTasks)
                {
                    if (repairTask.State != RepairTaskState.Completed)
                    {
                        await FabricRepairTasks.CancelRepairTaskAsync(repairTask, fabricClient);
                    }
                }
            }
            catch (FabricException)
            {
                throw;
            }
        }

        [ClassCleanup]
        public static async Task TestClassCleanupAsync()
        {
            await CleanupTestRepairJobsAsync();
        }

        /* GuanLogic Tests */
        // Currently, the tests below validate logic rules and the successful scheduling of related local repair jobs.

        // This test ensures your shipping rule files (the guan files located in Config/LogicRules folder)
        // contain correctly written rules and that the related local repair job is successfully created.
        [TestMethod]
        public async Task TestGuanLogic_AllRules_FabricHealer_EnsureWellFormedRules_QueryInitialized()
        {
            if (!IsLocalSFRuntimePresent())
            {
                throw new InternalTestFailureException("You must run this test with an active local (dev) SF cluster.");
            }

            FabricHealerManager.ConfigSettings = new ConfigSettings(context)
            {
                TelemetryEnabled = false
            };

            // This will be the data used to create a repair task.
            var repairData = new TelemetryData
            {
                ApplicationName = "fabric:/test",
                NodeName = "TEST_0",
                Code = SupportedErrorCodes.AppErrorMemoryMB,
                HealthState = HealthState.Warning,
                ServiceName = "fabric:/test0/service0",
                Value = 1024.0,
                RepairPolicy = new RepairPolicy
                {
                    RepairId = $"Test42_{SupportedErrorCodes.AppErrorMemoryMB}"
                }
            };

            var executorData = new RepairExecutorData
            {
                RepairData = repairData
            };

            foreach (var file in Directory.GetFiles(FHRulesDirectory))
            {
                List<string> repairRules = ParseRulesFile(await File.ReadAllLinesAsync(file, token));

                try
                {
                    await TestInitializeGuanAndRunQuery(repairData, repairRules, executorData);
                }
                catch (GuanException ge)
                {
                    throw new AssertFailedException(ge.Message, ge);
                }
            }
        }

        // This test ensures your test rules housed in testrules_wellformed file or in fact correct.
        [TestMethod]
        public async Task TestGuanLogicRule_GoodRule_QueryInitialized()
        {
            if (!IsLocalSFRuntimePresent())
            {
                throw new InternalTestFailureException("You must run this test with an active local (dev) SF cluster.");
            }

            FabricHealerManager.ConfigSettings = new ConfigSettings(context)
            {
                TelemetryEnabled = false
            };

            string testRulesFilePath = Path.Combine(Environment.CurrentDirectory, "testrules_wellformed");
            string[] rules = await File.ReadAllLinesAsync(testRulesFilePath, token);
            List<string> repairRules = ParseRulesFile(rules);
            var repairData = new TelemetryData
            {
                ApplicationName = "fabric:/test0",
                NodeName = "TEST_0",
                Metric = "Memory",
                HealthState = HealthState.Warning,
                Code = SupportedErrorCodes.AppErrorMemoryMB,
                ServiceName = "fabric:/test0/service0",
                Value = 42,
                ReplicaId = default,
                PartitionId = default,
                RepairPolicy = new RepairPolicy
                {
                    RepairId = $"Test42_{SupportedErrorCodes.AppErrorMemoryMB}"
                }
            };

            var executorData = new RepairExecutorData
            {
                RepairData = repairData
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
            if (!IsLocalSFRuntimePresent())
            {
                throw new InternalTestFailureException("You must run this test with an active local (dev) SF cluster.");
            }

            FabricHealerManager.ConfigSettings = new ConfigSettings(context)
            {
                TelemetryEnabled = false
            };

            string[] rules = await File.ReadAllLinesAsync(Path.Combine(Environment.CurrentDirectory, "testrules_malformed"), token);
            List<string> repairAction = ParseRulesFile(rules);

            var repairData = new TelemetryData
            {
                ApplicationName = "fabric:/test0",
                NodeName = "TEST_0",
                Metric = "Memory",
                HealthState = HealthState.Warning,
                Code = SupportedErrorCodes.AppErrorMemoryMB,
                ServiceName = "fabric:/test0/service0",
                Value = 42,
                ReplicaId = default,
                PartitionId = default,
                RepairPolicy = new RepairPolicy
                {
                    RepairId = $"Test42_{SupportedErrorCodes.AppErrorMemoryMB}"
                }
            };

            var executorData = new RepairExecutorData
            {
                RepairData = repairData
            };

            await Assert.ThrowsExceptionAsync<GuanException>(async () => { await TestInitializeGuanAndRunQuery(repairData, repairAction, executorData); });
        }

        /* private Helpers */

        private async Task TestInitializeGuanAndRunQuery(TelemetryData repairData, List<string> repairRules, RepairExecutorData executorData)
        {
            var fabricClient = new FabricClient();
            var repairTaskManager = new RepairTaskManager(fabricClient, context, token);
            var repairTaskEngine = new RepairTaskEngine(fabricClient);

            // Add predicate types to functor table. Note that all health information data from FO are automatically passed to all predicates.
            // This enables access to various health state values in any query. See Mitigate() in rules files, for examples.
            FunctorTable functorTable = new FunctorTable();

            // Add external helper predicates.
            functorTable.Add(CheckFolderSizePredicateType.Singleton(RepairConstants.CheckFolderSize, repairTaskManager, repairData));
            functorTable.Add(GetRepairHistoryPredicateType.Singleton(RepairConstants.GetRepairHistory, repairTaskManager, repairData));
            functorTable.Add(GetHealthEventHistoryPredicateType.Singleton(RepairConstants.GetHealthEventHistory, repairTaskManager, repairData));
            functorTable.Add(CheckInsideRunIntervalPredicateType.Singleton(RepairConstants.CheckInsideRunInterval, repairTaskManager, repairData));
            functorTable.Add(EmitMessagePredicateType.Singleton(RepairConstants.EmitMessage, repairTaskManager));

            // Add external repair predicates.
            functorTable.Add(DeleteFilesPredicateType.Singleton(RepairConstants.DeleteFiles, repairTaskManager, repairData));
            functorTable.Add(RestartCodePackagePredicateType.Singleton(RepairConstants.RestartCodePackage, repairTaskManager, repairData));
            functorTable.Add(RestartFabricNodePredicateType.Singleton(RepairConstants.RestartFabricNode, repairTaskManager, executorData, repairTaskEngine, repairData));
            functorTable.Add(RestartFabricSystemProcessPredicateType.Singleton(RepairConstants.RestartFabricSystemProcess, repairTaskManager, repairData));
            functorTable.Add(RestartReplicaPredicateType.Singleton(RepairConstants.RestartReplica, repairTaskManager, repairData));
            functorTable.Add(RestartMachinePredicateType.Singleton(RepairConstants.RestartVM, repairTaskManager, repairData));

            // Parse rules
            Module module = Module.Parse("external", repairRules, functorTable);

            // Create guan query dispatcher.
            var queryDispatcher = new GuanQueryDispatcher(module);

            /* Bind default arguments to goal (Mitigate). */

            List<CompoundTerm> compoundTerms = new List<CompoundTerm>();

            // Mitigate is the head of the rules used in FH. It's the Goal that Guan will try to accomplish based on the logical expressions (or subgoals) that form a given rule.
            CompoundTerm compoundTerm = new CompoundTerm("Mitigate");

            // The type of metric that led FO to generate the unhealthy evaluation for the entity (App, Node, VM, Replica, etc).
            // We rename these for brevity for simplified use in logic rule composition (e;g., MetricName="Threads" instead of MetricName="Total Thread Count").
            repairData.Metric = SupportedErrorCodes.GetMetricNameFromErrorCode(repairData.Code);

            // These args hold the related values supplied by FO and are available anywhere Mitigate is used as a rule head.
            compoundTerm.AddArgument(new Constant(repairData.ApplicationName), RepairConstants.AppName);
            compoundTerm.AddArgument(new Constant(repairData.Code), RepairConstants.ErrorCode);
            compoundTerm.AddArgument(new Constant(Enum.GetName(typeof(HealthState), repairData.HealthState)), RepairConstants.HealthState);
            compoundTerm.AddArgument(new Constant(repairData.Metric), RepairConstants.MetricName);
            compoundTerm.AddArgument(new Constant(repairData.NodeName), RepairConstants.NodeName);
            compoundTerm.AddArgument(new Constant(repairData.NodeType), RepairConstants.NodeType);
            compoundTerm.AddArgument(new Constant(repairData.ObserverName), RepairConstants.ObserverName);
            compoundTerm.AddArgument(new Constant(repairData.OS), RepairConstants.OS);
            compoundTerm.AddArgument(new Constant(repairData.ServiceName), RepairConstants.ServiceName);
            compoundTerm.AddArgument(new Constant(repairData.SystemServiceProcessName), RepairConstants.SystemServiceProcessName);
            compoundTerm.AddArgument(new Constant(repairData.PartitionId), RepairConstants.PartitionId);
            compoundTerm.AddArgument(new Constant(repairData.ReplicaId), RepairConstants.ReplicaOrInstanceId);
            compoundTerm.AddArgument(new Constant(Convert.ToInt64(repairData.Value)), RepairConstants.MetricValue);
            compoundTerms.Add(compoundTerm);

            await queryDispatcher.RunQueryAsync(compoundTerms);
        }

        private static List<string> ParseRulesFile(string[] rules)
        {
            var repairRules = new List<string>();
            int ptr1 = 0, ptr2 = 0;
            rules = rules.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

            while (ptr1 < rules.Length && ptr2 < rules.Length)
            {
                // Single line comments removal.
                if (rules[ptr2].TrimStart().StartsWith("##"))
                {
                    ptr1++;
                    ptr2++;
                    continue;
                }

                if (rules[ptr2].EndsWith("."))
                {
                    if (ptr1 == ptr2)
                    {
                        repairRules.Add(rules[ptr2].Remove(rules[ptr2].Length - 1, 1));
                    }
                    else
                    {
                        string rule = rules[ptr1].TrimEnd(' ');

                        for (int i = ptr1 + 1; i <= ptr2; i++)
                        {
                            rule = rule + ' ' + rules[i].Replace('\t', ' ').TrimStart(' ');
                        }

                        repairRules.Add(rule.Remove(rule.Length - 1, 1));
                    }
                    ptr2++;
                    ptr1 = ptr2;
                }
                else
                {
                    ptr2++;
                }
            }

            return repairRules;
        }

        private async Task<(bool, TelemetryData data)> 
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
            _ = JsonSerializationUtility.TryDeserialize(healthEventWarning.HealthInformation.Description, out TelemetryData data);

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

        static readonly RepairFacts RepairFactsServiceTarget = new RepairFacts
        {
            ServiceName = "fabric:/GettingStartedApplication/MyActorService",
            NodeName = NodeName,
            // Specifying Source is Required for unit tests.
            // For unit tests, there is no FabricRuntime static, so FHProxy, which utilizes this type, will fail unless Source is provided here.
            Source = "fabric:/test"
        };

        // This specifies that you want FabricHealer to repair a Fabric node named _Node_0. The only supported Fabric node repair in FabricHealer is a Restart.
        // Related rules can be found in FabricNodeRules.guan file in the FabricHealer project's PackageRoot/Config/LogicRules folder.
        // So, implicitly, this means you want FabricHealer to restart _Node_0. By default, if you only supply NodeName, then FabricHealerProxy assumes the target EntityType is Node.
        static readonly RepairFacts RepairFactsNodeTarget = new RepairFacts
        {
            NodeName = NodeName,
            // Specifying Source is Required for unit tests.
            // For unit tests, there is no FabricRuntime static, so FHProxy, which utilizes this type, will fail unless Source is provided here.
            Source = "fabric:/Test"
        };

        // Initiate a reboot of the machine hosting the specified Fabric node, _Node_4. This will be executed by the InfrastructureService for the related node type.
        // The related logic rules for this repair target are housed in FabricHealer's MachineRules.guan file.
        static readonly RepairFacts RepairFactsMachineTarget = new RepairFacts
        {
            NodeName = NodeName,
            EntityType = FabricHealer.EntityType.Machine,
            // Specifying Source is Required for unit tests.
            // For unit tests, there is no FabricRuntime static, so FHProxy, which utilizes this type, will fail unless Source is provided here.
            Source = "fabric:/Test"
        };

        // Restart system service process.
        static readonly RepairFacts SystemServiceRepairFacts = new RepairFacts
        {
            ApplicationName = "fabric:/System",
            NodeName = NodeName,
            SystemServiceProcessName = "FabricDCA",
            ProcessId = 73588,
            Code = SupportedErrorCodes.AppWarningMemoryMB,
            // Specifying Source is Required for unit tests.
            // For unit tests, there is no FabricRuntime static, so FHProxy, which utilizes this type, will fail unless Source is provided here.
            Source = "fabric:/Test"
        };

        // Disk - Delete files. This only works if FabricHealer instance is present on the same target node.
        // Note the rules in FabricHealer\PackageRoot\LogicRules\DiskRules.guan file in the FabricHealer project.
        static readonly RepairFacts DiskRepairFacts = new RepairFacts
        {
            NodeName = NodeName,
            EntityType = FabricHealer.EntityType.Disk,
            Metric = SupportedMetricNames.DiskSpaceUsageMb,
            Code = SupportedErrorCodes.NodeWarningDiskSpaceMB,
            // Specifying Source is Required for unit tests.
            // For unit tests, there is no FabricRuntime static, so FHProxy, which utilizes this type, will fail unless Source is provided here.
            Source = "fabric:/Test"
        };

        // For use in the IEnumerable<RepairFacts> RepairEntityAsync overload.
        static readonly List<RepairFacts> RepairFactsList = new List<RepairFacts>
        {
            DiskRepairFacts,
            RepairFactsMachineTarget,
            RepairFactsNodeTarget,
            RepairFactsServiceTarget,
            SystemServiceRepairFacts,
        };

        [TestMethod]
        public async Task FabricHealerProxy_Restart_Service_Generates_Entity_Health_Warning()
        {
            if (!IsLocalSFRuntimePresent())
            {
                throw new InternalTestFailureException("You must run this test with an active local (dev) SF cluster.");
            }

            // This will put the entity into Warning with a specially-crafted Health Event description (serialized instance of ITelemetryData type).
            await Proxy.Instance.RepairEntityAsync(RepairFactsServiceTarget, token);
            var (generatedWarning, data) = await IsEntityInWarningStateAsync(null, RepairFactsServiceTarget.ServiceName);

            // FHProxy creates or renames Source with trailing id ("FabricHealerProxy");
            Assert.IsTrue(RepairFactsServiceTarget.Source.EndsWith(FHProxyId));
            Assert.IsTrue(generatedWarning);
            Assert.IsTrue(data is TelemetryData);
        }

        [TestMethod]
        public async Task FabricHealerProxy_Restart_Node_Generates_Entity_Health_Warning()
        {
            if (!IsLocalSFRuntimePresent())
            {
                throw new InternalTestFailureException("You must run this test with an active local (dev) SF cluster.");
            }

            // This will put the entity into Warning with a specially-crafted Health Event description (serialized instance of ITelemetryData type).
            await Proxy.Instance.RepairEntityAsync(RepairFactsNodeTarget, token);
            var (generatedWarning, data) = await IsEntityInWarningStateAsync(null, null, NodeName);

            // FHProxy creates or renames Source with trailing id ("FabricHealerProxy");
            Assert.IsTrue(RepairFactsNodeTarget.Source.EndsWith(FHProxyId));
            Assert.IsTrue(generatedWarning);
            Assert.IsTrue(data is TelemetryData);
        }

        [TestMethod]
        public async Task FHProxy_MissingFact_Generates_MissingRepairFactsException()
        {
            if (!IsLocalSFRuntimePresent())
            {
                throw new InternalTestFailureException("You must run this test with an active local (dev) SF cluster.");
            }

            var repairFacts = new RepairFacts
            {
                ServiceName = "fabric:/foo/bar",
                // Specifying Source is Required for unit tests.
                // For unit tests, there is no FabricRuntime static, so FHProxy, which utilizes this type, will fail unless Source is provided here.
                Source = "fabric:/Test"
            };

            await Assert.ThrowsExceptionAsync<MissingRepairFactsException>(async () => { await Proxy.Instance.RepairEntityAsync(repairFacts, token); });
        }

        [TestMethod]
        public async Task FHProxy_MissingFact_Generates_ServiceNotFoundException()
        {
            if (!IsLocalSFRuntimePresent())
            {
                throw new InternalTestFailureException("You must run this test with an active local (dev) SF cluster.");
            }

            var repairFacts = new RepairFacts
            {
                ServiceName = "fabric:/foo/bar",
                NodeName = NodeName,
                // Specifying Source is Required for unit tests.
                // For unit tests, there is no FabricRuntime static, so FHProxy, which utilizes this type, will fail unless Source is provided here.
                Source = "fabric:/Test"
            };

            await Assert.ThrowsExceptionAsync<ServiceNotFoundException>(async () => { await Proxy.Instance.RepairEntityAsync(repairFacts, token); });
        }

        [TestMethod]
        public async Task FHProxy_MissingFact_Generates_NodeNotFoundException()
        {
            if (!IsLocalSFRuntimePresent())
            {
                throw new InternalTestFailureException("You must run this test with an active local (dev) SF cluster.");
            }

            var repairFacts = new RepairFacts
            {
                NodeName = "_Node_007x",
                // No need for Source here as an invalid node will be detected before the Source value matters.
            };

            await Assert.ThrowsExceptionAsync<NodeNotFoundException>(async () => { await Proxy.Instance.RepairEntityAsync(repairFacts, token); });
        }
    }
}
