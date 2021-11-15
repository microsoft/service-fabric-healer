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

namespace FHTest
{
    [TestClass]
    public class FHUnitTests
    {
        private static readonly Uri ServiceName = new Uri("fabric:/app/service");
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

        // Set this to the full path to your Rules directory in the FabricHealer project's PackageRoot\Config directory.
        // e.g., if developing on Windows, then something like @"C:\Users\[me]\source\repos\service-fabric-healer\FabricHealer\PackageRoot\Config\LogicRules\";
        private const string FHRulesDirectory = @"C:\Users\[me]\source\repos\service-fabric-healer\FabricHealer\PackageRoot\Config\LogicRules\";

        /* GuanLogic Tests */
        // TODO: Add more tests.

        // This test ensures your actual rule files contain legitimate rules. This will catch bugs in your
        // logic. Of course, you should have caught these flaws in your end-to-end tests. This is just an extra precaution.
        [TestMethod]
        public async Task TestGuanLogic_AllRules_FabricHealer_EnsureWellFormedRules_QueryInitialized()
        {
            FabricHealerManager.ConfigSettings = new ConfigSettings(context)
            {
                TelemetryEnabled = false
            };

            var foHealthData = new TelemetryData
            {
                ApplicationName = "fabric:/test0",
                NodeName = "TEST_0",
                RepairId = "Test42",
                Code = FOErrorWarningCodes.AppErrorMemoryMB,
                ServiceName = "fabric/test0/service0",
            };

            var executorData = new RepairExecutorData
            {
                RepairPolicy = new RepairPolicy(),
            };

            foreach (var file in Directory.GetFiles(FHRulesDirectory))
            {
                List<string> repairRules = ParseRulesFile((await File.ReadAllLinesAsync(file, token)).ToList());

                try
                {
                    Assert.IsTrue(await TestInitializeGuanAndRunQuery(foHealthData, repairRules, executorData).ConfigureAwait(true));
                }
                catch (GuanException ge)
                {
                    Console.WriteLine(ge.ToString());
                    throw;
                }
            }

            Assert.IsTrue(true);
        }

        // This test ensures a given rule can successfully be turned into a GL query. 
        // This means that the rule is well-formed logic and that the referenced predicates exist.
        // So, if the rule is malformed or not a logic rule or no predicate exists as written, this test will fail.
        [TestMethod]
        public async Task TestGuanLogicRule_GoodRule_QueryInitialized()
        {
            FabricHealerManager.ConfigSettings = new ConfigSettings(context)
            {
                TelemetryEnabled = false
            };
            string testRulesFilePath = Path.Combine(Environment.CurrentDirectory, "testrules_wellformed");
            string[] rules = await File.ReadAllLinesAsync(testRulesFilePath, token).ConfigureAwait(true);
            List<string> repairRules = ParseRulesFile(rules.ToList());
            var foHealthData = new TelemetryData
            {
                ApplicationName = "fabric:/test0",
                NodeName = "TEST_0",
                Metric = "Memory",
                RepairId = "Test42",
                Code = FOErrorWarningCodes.AppErrorMemoryMB,
                ServiceName = "fabric/test0/service0",
                Value = 42,
                ReplicaId = default,
                PartitionId = default(Guid).ToString(),
            };

            var executorData = new RepairExecutorData
            {
                RepairPolicy = new RepairPolicy { RepairAction = RepairActionType.RestartCodePackage },
            };

            Assert.IsTrue(await TestInitializeGuanAndRunQuery(foHealthData, repairRules, executorData).ConfigureAwait(true));
        }

        // All rules in target rules file are malformed. They should all lead to GuanExceptions.
        // If they do not lead to a GuanException from TestInitializeGuanAndRunQuery, then this test will fail.
        [TestMethod]
        public async Task TestGuanLogicRule_BadRule_ShouldThrowGuanException()
        {
            FabricHealerManager.ConfigSettings = new ConfigSettings(context)
            {
                TelemetryEnabled = false
            };
            string[] rules = await File.ReadAllLinesAsync(Path.Combine(Environment.CurrentDirectory, "testrules_malformed"), token).ConfigureAwait(true);
            List<string> repairAction = ParseRulesFile(rules.ToList());

            var foHealthData = new TelemetryData
            {
                ApplicationName = "fabric:/test0",
                NodeName = "TEST_0",
                Metric = "Memory",
                RepairId = "Test42",
                Code = FOErrorWarningCodes.AppErrorMemoryMB,
                ServiceName = "fabric/test0/service0",
                Value = 42,
                ReplicaId = default,
                PartitionId = default(Guid).ToString(),
            };

            var executorData = new RepairExecutorData
            {
                RepairPolicy = new RepairPolicy { RepairAction = RepairActionType.RestartCodePackage },
            };

            await Assert.ThrowsExceptionAsync<GuanException>(async () => { await TestInitializeGuanAndRunQuery(foHealthData, repairAction, executorData); });
        }

        /* FH Repair Scheduler Tests */
        // TODO.

        /* FH Repair Excecutor Tests */
        // TODO.

        [ClassCleanup]
        public static void TestClassCleanup()
        {
        }

        /* private Helpers */

        private Task<bool> TestInitializeGuanAndRunQuery(
                                    TelemetryData foHealthData,
                                    List<string> repairRules,
                                    RepairExecutorData executorData)
        {
            var fabricClient = new FabricClient(FabricClientRole.Admin);
            var repairTaskManager = new RepairTaskManager(fabricClient, context, token);
            var repairTaskEngine = new RepairTaskEngine(fabricClient);

            // Add predicate types to functor table. Note that all health information data from FO are automatically passed to all predicates.
            // This enables access to various health state values in any query. See Mitigate() in rules files, for examples.
            FunctorTable functorTable = new FunctorTable();

            // Add external helper predicates.
            functorTable.Add(CheckFolderSizePredicateType.Singleton(RepairConstants.CheckFolderSize, repairTaskManager, foHealthData));
            functorTable.Add(GetRepairHistoryPredicateType.Singleton(RepairConstants.GetRepairHistory, repairTaskManager, foHealthData));
            functorTable.Add(GetHealthEventHistoryPredicateType.Singleton(RepairConstants.GetHealthEventHistory, repairTaskManager, foHealthData));
            functorTable.Add(CheckInsideRunIntervalPredicateType.Singleton(RepairConstants.CheckInsideRunInterval, repairTaskManager, foHealthData));
            functorTable.Add(EmitMessagePredicateType.Singleton(RepairConstants.EmitMessage, repairTaskManager));

            // Add external repair predicates.
            functorTable.Add(DeleteFilesPredicateType.Singleton(RepairConstants.DeleteFiles, repairTaskManager, foHealthData));
            functorTable.Add(RestartCodePackagePredicateType.Singleton(RepairConstants.RestartCodePackage, repairTaskManager, foHealthData));
            functorTable.Add(RestartFabricNodePredicateType.Singleton(RepairConstants.RestartFabricNode, repairTaskManager, executorData, repairTaskEngine, foHealthData));
            functorTable.Add(RestartFabricSystemProcessPredicateType.Singleton(RepairConstants.RestartFabricSystemProcess, repairTaskManager, foHealthData));
            functorTable.Add(RestartReplicaPredicateType.Singleton(RepairConstants.RestartReplica, repairTaskManager, foHealthData));
            functorTable.Add(RestartVMPredicateType.Singleton(RepairConstants.RestartVM, repairTaskManager, foHealthData));

            // Parse rules
            Module module = Module.Parse("external", repairRules, functorTable);

            // Create guan query dispatcher.
            _ = new GuanQueryDispatcher(module);

            /* Bind default arguments to goal (Mitigate). */

            List<CompoundTerm> compoundTerms = new List<CompoundTerm>();

            // Mitigate is the head of the rules used in FH. It's the Goal that Guan will try to accomplish based on the logical expressions (or subgoals) that form a given rule.
            CompoundTerm compoundTerm = new CompoundTerm("Mitigate");

            // The type of metric that led FO to generate the unhealthy evaluation for the entity (App, Node, VM, Replica, etc).
            // We rename these for brevity for simplified use in logic rule composition (e;g., MetricName="Threads" instead of MetricName="Total Thread Count").
            foHealthData.Metric = FOErrorWarningCodes.GetMetricNameFromCode(foHealthData.Code);

            // These args hold the related values supplied by FO and are available anywhere Mitigate is used as a rule head.
            compoundTerm.AddArgument(new Constant(foHealthData.ApplicationName), RepairConstants.AppName);
            compoundTerm.AddArgument(new Constant(foHealthData.Code), RepairConstants.FOErrorCode);
            compoundTerm.AddArgument(new Constant(foHealthData.Metric), RepairConstants.MetricName);
            compoundTerm.AddArgument(new Constant(foHealthData.NodeName), RepairConstants.NodeName);
            compoundTerm.AddArgument(new Constant(foHealthData.NodeType), RepairConstants.NodeType);
            compoundTerm.AddArgument(new Constant(foHealthData.OS), RepairConstants.OS);
            compoundTerm.AddArgument(new Constant(foHealthData.ServiceName), RepairConstants.ServiceName);
            compoundTerm.AddArgument(new Constant(foHealthData.SystemServiceProcessName), RepairConstants.SystemServiceProcessName);
            compoundTerm.AddArgument(new Constant(foHealthData.PartitionId), RepairConstants.PartitionId);
            compoundTerm.AddArgument(new Constant(foHealthData.ReplicaId), RepairConstants.ReplicaOrInstanceId);
            compoundTerm.AddArgument(new Constant(Convert.ToInt64(foHealthData.Value)), RepairConstants.MetricValue);
            compoundTerms.Add(compoundTerm);

            return Task.FromResult(true);
        }

        private List<string> ParseRulesFile(List<string> rules)
        {
            var repairRules = new List<string>();
            int ptr1 = 0; int ptr2 = 0;
            rules = rules.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

            while (ptr1 < rules.Count && ptr2 < rules.Count)
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
    }
}
