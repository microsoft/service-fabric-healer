// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Threading.Tasks;
using Guan.Common;
using Guan.Logic;
using System;
using FabricHealer.Utilities.Telemetry;

namespace FabricHealer.Repair.Guan
{
    public class GetRepairHistoryPredicateType : PredicateType
    {
        private static RepairTaskHelper RepairTaskHelper;
        private static TelemetryData FOHealthData;
        private static GetRepairHistoryPredicateType Instance;

        class Resolver : GroundPredicateResolver
        {
            public Resolver(
                CompoundTerm input, 
                Constraint constraint, 
                QueryContext context)
                : base(input, constraint, context, 1)
            {

            }

            protected override Task<Term> GetNextTermAsync()
            {
                string repairAction = (string)Input.Arguments[2].Value.GetEffectiveTerm().GetValue();
                long repairCount = 0;
                DateTime lastRunTime = DateTime.MinValue;

                if (!Enum.TryParse(repairAction, true, out RepairAction repairActionType))
                {
                    throw new GuanException("You must specify a repair action predicate name that exists.");
                }

                switch (repairActionType)
                {
                    case RepairAction.DeleteFiles:

                        if (RepairTaskHelper.CompletedDiskRepairs.ContainsKey(FOHealthData.RepairId))
                        {
                            lastRunTime = RepairTaskHelper.CompletedDiskRepairs[FOHealthData.RepairId].LastRunTime;
                            repairCount = RepairTaskHelper.CompletedDiskRepairs[FOHealthData.RepairId].RepairCount;
                        }
                        break;

                    case RepairAction.RestartCodePackage:
                        
                        if (RepairTaskHelper.CompletedCodePackageRepairs.ContainsKey(FOHealthData.RepairId))
                        {
                            lastRunTime = RepairTaskHelper.CompletedCodePackageRepairs[FOHealthData.RepairId].LastRunTime;
                            repairCount = RepairTaskHelper.CompletedCodePackageRepairs[FOHealthData.RepairId].RepairCount;
                        }
                        break;

                    case RepairAction.RestartFabricNode:

                        if (RepairTaskHelper.CompletedFabricNodeRepairs.ContainsKey(FOHealthData.RepairId))
                        {
                            lastRunTime = RepairTaskHelper.CompletedFabricNodeRepairs[FOHealthData.RepairId].LastRunTime;
                            repairCount = RepairTaskHelper.CompletedFabricNodeRepairs[FOHealthData.RepairId].RepairCount;
                        }
                        break;

                    case RepairAction.RestartReplica:
                    case RepairAction.RemoveReplica:
                        
                        if (RepairTaskHelper.CompletedReplicaRepairs.ContainsKey(FOHealthData.RepairId))
                        {
                            lastRunTime = RepairTaskHelper.CompletedReplicaRepairs[FOHealthData.RepairId].LastRunTime;
                            repairCount = RepairTaskHelper.CompletedReplicaRepairs[FOHealthData.RepairId].RepairCount;
                        }
                        break;

                    case RepairAction.RestartVM:
                        
                        if (RepairTaskHelper.CompletedVmRepairs.ContainsKey(FOHealthData.RepairId))
                        {
                            lastRunTime = RepairTaskHelper.CompletedVmRepairs[FOHealthData.RepairId].LastRunTime;
                            repairCount = RepairTaskHelper.CompletedVmRepairs[FOHealthData.RepairId].RepairCount;
                        }
                        break;
                    /* TODO...
                    case RepairAction.PauseFabricNode:
                        break;
                    case RepairAction.RemoveFabricNodeState:
                        break;
                    */
                    default:
                        throw new GuanException("Unsupported Repair Action: Can't unify.");
                }

                var result = new CompoundTerm(Instance, null);

                result.AddArgument(new Constant(repairCount), "0");
                result.AddArgument(new Constant(lastRunTime), "1");

                return Task.FromResult<Term>(result);
            }
        }

        public static GetRepairHistoryPredicateType Singleton(
            string name,
            RepairTaskHelper repairTaskHelper,
            TelemetryData foHealthData)
        {
            RepairTaskHelper = repairTaskHelper;
            FOHealthData = foHealthData;

            return Instance ??= new GetRepairHistoryPredicateType(name);
        }

        private GetRepairHistoryPredicateType(
            string name)
            : base(name, true, 3, 3)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }

        public override void AdjustTerm(CompoundTerm term, Rule rule)
        {
            if (!(term.Arguments[0].Value is IndexedVariable))
            {
                throw new GuanException("The first argument of GetRepairHistoryPredicateType must be a variable: {0}", term);
            }

            if (!(term.Arguments[1].Value is IndexedVariable))
            {
                throw new GuanException("The second argument of GetRepairHistoryPredicateType must be a variable: {1}", term);
            }

            if (!(term.Arguments[2].Value is Constant))
            {
                throw new GuanException("The third argument of GetRepairHistoryPredicateType must be a constant: {2}", term);
            }
        }
    }
}
