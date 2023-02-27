// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using Guan.Logic;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FabricHealer.Repair.Guan
{
    public class LogRulePredicateType : PredicateType
    {
        private static LogRulePredicateType Instance;
        private static TelemetryData RepairData;

        private class Resolver : BooleanPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

            }

            protected override async Task<bool> CheckAsync()
            {
                string ruleFileName = FabricHealerManager.CurrentlyExecutingLogicRulesFileName, rule = string.Empty;
                int lineNumber = 0;

                string ruleFilePath = 
                    Path.Combine(
                        FabricHealerManager.ServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Path,
                        "LogicRules",
                        ruleFileName);

                if (!File.Exists(ruleFilePath))
                {
                    throw new GuanException($"Specified rule file path does not exist: {ruleFilePath}");
                }

                try
                {
                    string[] lines = File.ReadLines(ruleFilePath).ToArray();

                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i];

                        if (line.Contains($":- {RepairConstants.LogRule}", StringComparison.OrdinalIgnoreCase))
                        {
                            lineNumber = i;
                            line = lines[lineNumber];
                            rule = line;
                            
                            while (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("##"))
                            {
                                lineNumber++;
                                line = lines[lineNumber];
                            }

                            // custom rule formatting support.
                            if (line.TrimEnd().EndsWith(','))
                            {
                                for (int j = lineNumber; lines[j].TrimEnd().EndsWith(','); j++)
                                {
                                    rule += " " + lines[j + 1].Replace('\t', ' ').Trim();
                                    lineNumber = j;
                                }
                            }

                            break;
                        }
                    }

                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"{ruleFileName}#{lineNumber + 1}_{RepairData.RepairPolicy.ProcessName ?? RepairData.NodeName}",
                            $"Executing logic rule \'{rule}\'",
                            FabricHealerManager.Token);
                }
                catch (Exception e) when (e is ArgumentException || e is IOException || e is SystemException)
                {
#if DEBUG
                    string message = $"LogRule predicate failure => Unable to read {ruleFileName}: {e.Message}.";
                    FabricHealerManager.RepairLogger.LogWarning(message);
                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"LogRule::{ruleFileName}::Failure",
                            message,
                            FabricHealerManager.Token);

                    return false;
# endif
                }

                return true;
            }
        }

        public static LogRulePredicateType Singleton(string name, TelemetryData repairData)
        {
            RepairData = repairData;
            return Instance ??= new LogRulePredicateType(name);
        }

        private LogRulePredicateType(string name) : base(name, true, 0, 0)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}

