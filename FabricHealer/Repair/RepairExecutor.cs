// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Result;
using System.Fabric.Query;
using System.Fabric.Health;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using System.Fabric.Repair;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.ComponentModel;
using Newtonsoft.Json;
using System.Fabric.Description;

namespace FabricHealer.Repair
{
    public class RepairExecutor
    {
        private const double MaxWaitTimeMinutesForNodeOperation = 60.0;
        private readonly TelemetryUtilities telemetryUtilities;
        private readonly StatelessServiceContext serviceContext;

        private bool IsOneNodeCluster
        {
            get;
        }

        public RepairExecutor(StatelessServiceContext context, CancellationToken token)
        {
            serviceContext = context;
            telemetryUtilities = new TelemetryUtilities(context);

            try
            {
                if (FabricHealerManager.ConfigSettings == null)
                {
                    return;
                }

                IsOneNodeCluster =
                        FabricHealerManager.FabricClientSingleton.QueryManager.GetNodeListAsync(null, FabricHealerManager.ConfigSettings.AsyncTimeout, token).GetAwaiter().GetResult().Count == 1;
            }
            catch (FabricException fe)
            {
                FabricHealerManager.RepairLogger.LogWarning($"Unable to determine cluster size:{Environment.NewLine}{fe}");
            }
        }

        public async Task<RestartDeployedCodePackageResult> RestartDeployedCodePackageAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            try
            {
                string actionMessage =
                    "Attempting to restart deployed code package for service " +
                    $"{repairData.ServiceName} " +
                    $"({repairData.ReplicaId}) on Node {repairData.NodeName}.";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RestartDeployedCodePackageAsync::Starting",
                        actionMessage,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                PartitionSelector partitionSelector = PartitionSelector.PartitionIdOf(new Uri(repairData.ServiceName), (Guid)repairData.PartitionId);
                long replicaId = repairData.ReplicaId;
                Replica replica = null;

                // Verify target replica still exists.
                var replicaList = await FabricHealerManager.FabricClientSingleton.QueryManager.GetReplicaListAsync(
                                            (Guid)repairData.PartitionId,
                                            replicaId,
                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                            cancellationToken);
                
                if (replicaList.Any(r => r.ReplicaStatus == ServiceReplicaStatus.Ready))
                {
                    replica = replicaList.First(r => r.ReplicaStatus == ServiceReplicaStatus.Ready);
                }
                else
                {
                    await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RepairExecutor.RestartCodePackageAsync",
                            $"Execution failure: Replica {repairData.ReplicaId} " +
                            $"not found in partition {repairData.PartitionId}.",
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return null;
                }

                ReplicaSelector replicaSelector = ReplicaSelector.ReplicaIdOf(partitionSelector, replicaId);

                // There is a bug with Verify for Stateless services...
                // CompletionMode must be set to DoNotVerify for stateless services.
                CompletionMode completionMode = CompletionMode.DoNotVerify;

                if (replica.ServiceKind == ServiceKind.Stateful)
                {
                    completionMode = CompletionMode.Verify;
                }

                var restartCodePackageResult = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                        () =>
                                                        FabricHealerManager.FabricClientSingleton.FaultManager.RestartDeployedCodePackageAsync(
                                                            new Uri(repairData.ApplicationName),
                                                            replicaSelector,
                                                            completionMode, 
                                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                            cancellationToken), 
                                                        cancellationToken);

                if (restartCodePackageResult != null)
                {
                    UpdateRepairHistory(repairData);
                    ClearEntityHealthWarnings(repairData);

                    actionMessage =
                        "Successfully restarted deployed code package for service " +
                        $"{repairData.ServiceName} " +
                        $"({repairData.ReplicaId}) on Node {repairData.NodeName}.";

                    await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RestartDeployedCodePackageAsync::Success",
                            actionMessage,
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);
                }
                else
                {
                    actionMessage =
                       "Failed to restart deployed code package for service " +
                       $"{repairData.ServiceName} " +
                       $"({repairData.ReplicaId}) on Node {repairData.NodeName}.";

                    await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RestartDeployedCodePackageAsync::Failed",
                            actionMessage,
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);
                }

                return restartCodePackageResult;
            }
            catch (Exception e) when (e is FabricException || e is InvalidOperationException || e is TimeoutException)
            {              
                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "RepairExecutor.RestartCodePackageAsync",
                        $"Execution failure:{Environment.NewLine}{e}",
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                FabricHealerManager.RepairHistory.FailedRepairs++;
                return null;
            }
        }

        private static void UpdateRepairHistory(TelemetryData repairData)
        {
            string repairName = Enum.GetName(typeof(RepairActionType), repairData.RepairPolicy.RepairAction);

            if (!FabricHealerManager.RepairHistory.Repairs.ContainsKey(repairName))
            {
                FabricHealerManager.RepairHistory.Repairs.Add(repairName, (repairData.Source, 1));
            }
            else
            {
                double count = FabricHealerManager.RepairHistory.Repairs[repairName].Count + 1;
                FabricHealerManager.RepairHistory.Repairs[repairName] = (repairData.Source, count);
            }

            FabricHealerManager.RepairHistory.RepairCount++;
            FabricHealerManager.RepairHistory.SuccessfulRepairs++;
        }

        /// <summary>
        /// Safely restarts a Service Fabric Node instance.
        /// Algorithm:
        /// 1 Deactivate target node.
        /// 2 Wait for node to get into Disabled/Ok.
        /// 3 Restart node (which is the Fabric.exe kill API in FaultManager)
        /// 4 Wait for node to go Down.
        /// 5 Wait for node to get to Disabled/Ok.
        /// 5 Activate node.
        /// 6 Wait for node to get to Up/Ok.
        /// </summary>
        /// <param name="repairData">Repair configuration</param>
        /// <param name="repairTask">The scheduled Repair Task</param>
        /// <param name="cancellationToken">Task cancellation token</param>
        /// <returns></returns>
        public async Task<bool> SafeRestartFabricNodeAsync(
                                    TelemetryData repairData,
                                    RepairTask repairTask, 
                                    CancellationToken cancellationToken)
        {
            if (IsOneNodeCluster)
            {
                string info = "One node cluster detected. Aborting node restart operation.";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.SafeRestartFabricNodeAsync::NodeCount_1",
                        info,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }

            var nodeQueryDesc = new NodeQueryDescription
            {
                MaxResults = 5,
            };

            NodeList nodeList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () => FabricHealerManager.FabricClientSingleton.QueryManager.GetNodePagedListAsync(
                                                nodeQueryDesc,
                                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                cancellationToken),
                                        cancellationToken);

            if (nodeList.Count < 3)
            {
                string info = $"Unsupported repair for a {nodeList.Count} node cluster. Aborting fabric node restart operation.";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.SafeRestartFabricNodeAsync::NodeCount",
                        info,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                FabricHealerManager.RepairLogger.LogInfo(info);
                return false;
            }

            ServiceDescription serviceDesc =
               await FabricHealerManager.FabricClientSingleton.ServiceManager.GetServiceDescriptionAsync(serviceContext.ServiceName, FabricHealerManager.ConfigSettings.AsyncTimeout, cancellationToken);

            int instanceCount = (serviceDesc as StatelessServiceDescription).InstanceCount;

            if (instanceCount == -1)
            {
                bool isTargetNodeHostingFH = repairData.NodeName == serviceContext.NodeContext.NodeName;

                if (isTargetNodeHostingFH)
                {
                    return false;
                }
            }
          
            if (!nodeList.Any(n => n.NodeName == repairData.NodeName))
            {
                string info = $"Fabric node {repairData.NodeName} does not exist.";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.SafeRestartFabricNodeAsync::MissingNode",
                        info,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);
            }

            var nodeInstanceId = nodeList.First(n => n.NodeName == repairData.NodeName).NodeInstanceId;
            var stopwatch = new Stopwatch();
            var maxWaitTimeout = TimeSpan.FromMinutes(MaxWaitTimeMinutesForNodeOperation);
            string actionMessage = $"Attempting to safely restart Fabric node {repairData.NodeName} with InstanceId {nodeInstanceId}.";
            await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.SafeRestartFabricNodeAsyncAttemptingRestart",
                    actionMessage,
                    cancellationToken,
                    repairData,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);
            try
            {
                if (!JsonSerializationUtility.TryDeserialize(repairTask.ExecutorData, out RepairExecutorData executorData))
                {
                    return false;
                }

                if (executorData.LatestRepairStep == FabricNodeRepairStep.Scheduled)
                {
                    executorData.LatestRepairStep = FabricNodeRepairStep.Deactivate;

                    if (JsonSerializationUtility.TrySerialize(executorData, out string exData))
                    {
                        repairTask.ExecutorData = exData;
                    }
                    else
                    {
                        actionMessage = "Step = Deactivate => Did not successfully serialize executordata.";

                        await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                "RepairExecutor.SafeRestartFabricNodeAsyncAttemptingRestart::Deactivate",
                                actionMessage,
                                cancellationToken,
                                repairData,
                                FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                        return false;
                    }

                    await FabricHealerManager.FabricClientSingleton.RepairManager.UpdateRepairExecutionStateAsync(repairTask, FabricHealerManager.ConfigSettings.AsyncTimeout, cancellationToken);

                    // Deactivate the node with intent to restart. Several health checks will 
                    // take place to ensure safe deactivation, which includes giving services a
                    // chance to gracefully shut down, should they override OnAbort/OnClose.
                    await FabricHealerManager.FabricClientSingleton.ClusterManager.DeactivateNodeAsync(
                            repairData.NodeName,
                            NodeDeactivationIntent.Restart,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken);

                    stopwatch.Start();

                    // Wait for node to get into Disabled state.
                    while (stopwatch.Elapsed <= maxWaitTimeout)
                    {
                        var nodes = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                            () =>
                                            FabricHealerManager.FabricClientSingleton.QueryManager.GetNodeListAsync(
                                                repairData.NodeName,
                                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                cancellationToken), 
                                            cancellationToken);

                        if (nodes == null || nodes.Count == 0)
                        {
                            break;
                        }

                        Node targetNode = nodes[0];

                        // exit loop, this is the state we're looking for.
                        if (targetNode.NodeStatus == NodeStatus.Disabled)
                        {
                            break;
                        }

                        await Task.Delay(1000, cancellationToken);
                    }

                    stopwatch.Stop();
                    stopwatch.Reset();
                }
                
                if (executorData.LatestRepairStep == FabricNodeRepairStep.Deactivate)
                {
                    executorData.LatestRepairStep = FabricNodeRepairStep.Restart;

                    if (JsonSerializationUtility.TrySerialize(executorData, out string exData))
                    {
                        repairTask.ExecutorData = exData;
                    }
                    else
                    {
                        return false;
                    }

                    await FabricHealerManager.FabricClientSingleton.RepairManager.UpdateRepairExecutionStateAsync(repairTask, FabricHealerManager.ConfigSettings.AsyncTimeout, cancellationToken);

                    actionMessage = $"In Step Restart Node.{Environment.NewLine}{repairTask.ExecutorData}";

                    await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RepairExecutor.SafeRestartFabricNodeAsyncAttemptingRestart::RestartStep",
                            actionMessage,
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    // Now, restart node.
                    _ = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                () =>
                                FabricHealerManager.FabricClientSingleton.FaultManager.RestartNodeAsync(
                                    repairData.NodeName,
                                    nodeInstanceId,
                                    FabricHealerManager.ConfigSettings.AsyncTimeout,
                                    cancellationToken), 
                                cancellationToken);

                    stopwatch.Start();

                    // Wait for Disabled/OK
                    while (stopwatch.Elapsed <= maxWaitTimeout)
                    {
                        var nodes = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                            () =>
                                            FabricHealerManager.FabricClientSingleton.QueryManager.GetNodeListAsync(
                                                repairData.NodeName,
                                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                cancellationToken), 
                                            cancellationToken);

                        Node targetNode = nodes[0];

                        // Node is ready to be enabled.
                        if (targetNode.NodeStatus == NodeStatus.Disabled && targetNode.HealthState == HealthState.Ok)
                        {
                            break;
                        }

                        await Task.Delay(1000, cancellationToken);
                    }

                    stopwatch.Stop();
                    stopwatch.Reset();
                }

                if (executorData.LatestRepairStep == FabricNodeRepairStep.Restart)
                {
                    executorData.LatestRepairStep = FabricNodeRepairStep.Activate;

                    if (JsonSerializationUtility.TrySerialize(executorData, out string exData))
                    {
                        repairTask.ExecutorData = exData;
                    }
                    else
                    {
                        return false;
                    }

                    await FabricHealerManager.FabricClientSingleton.RepairManager.UpdateRepairExecutionStateAsync(repairTask, FabricHealerManager.ConfigSettings.AsyncTimeout, cancellationToken);

                    // Now, enable the node. 
                    await FabricHealerManager.FabricClientSingleton.ClusterManager.ActivateNodeAsync(repairData.NodeName, FabricHealerManager.ConfigSettings.AsyncTimeout, cancellationToken);

                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);

                    var nodes = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () =>
                                        FabricHealerManager.FabricClientSingleton.QueryManager.GetNodeListAsync(
                                            repairData.NodeName,
                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                            cancellationToken), 
                                        cancellationToken);

                    Node targetNode = nodes[0];

                    // Make sure activation request went through.
                    if (targetNode.NodeStatus == NodeStatus.Disabled && targetNode.HealthState == HealthState.Ok)
                    {
                        await FabricHealerManager.FabricClientSingleton.ClusterManager.ActivateNodeAsync(repairData.NodeName, FabricHealerManager.ConfigSettings.AsyncTimeout, cancellationToken);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                    UpdateRepairHistory(repairData);
                    return true;
                }

                FabricHealerManager.RepairHistory.FailedRepairs++;
                return false;
            }
            catch (Exception e) when (e is FabricException || e is OperationCanceledException || e is TimeoutException)
            {
                string err = $"Handled Exception restarting Fabric node {repairData.NodeName}, NodeInstanceId {nodeInstanceId}:{e.GetType().Name}";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.SafeRestartFabricNodeAsync::HandledException",
                        err,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                FabricHealerManager.RepairLogger.LogInfo(err);
                FabricHealerManager.RepairHistory.FailedRepairs++;
                return false;
            }
        }

        /// <summary>
        /// Restarts a stateful replica.
        /// </summary>
        /// <param name="repairData">repairData instance.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns></returns>
        public async Task<bool> RestartReplicaAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            string actionMessage = $"Attempting to restart stateful replica {repairData.ReplicaId} " +
                                   $"on partition {repairData.PartitionId} on node {repairData.NodeName}.";
            
            await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.RestartReplicaAsync::Start",
                    actionMessage,
                    cancellationToken,
                    repairData,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);
            try
            {
                // Make sure the replica still exists. \\

                var replicaList = await FabricHealerManager.FabricClientSingleton.QueryManager.GetReplicaListAsync(
                                            (Guid)repairData.PartitionId,
                                            repairData.ReplicaId,
                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                            cancellationToken);

                if (!replicaList.Any(r => r.ReplicaStatus == ServiceReplicaStatus.Ready))
                {
                    await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RepairExecutor.RestartReplicaAsync",
                            $"Execution failure: Stateful replica {repairData.ReplicaId} " +
                            $"not found in partition {repairData.PartitionId}.",
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return false;
                }

                try
                {
                    await FabricHealerManager.FabricClientSingleton.ServiceManager.RestartReplicaAsync(
                            repairData.NodeName,
                            (Guid)repairData.PartitionId,
                            repairData.ReplicaId,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken);
                }
                catch (FabricException fe)
                {
                    // This would mean the stateful service replica is volatile (no persisted state), so we have to Remove it.
                    if (fe.ErrorCode == FabricErrorCode.InvalidReplicaOperation && fe.InnerException.Message == "0x80071C3A")
                    {
                        return await RemoveReplicaAsync(repairData, cancellationToken);                    
                    }
                }

                string statusSuccess =
                        $"Successfully restarted stateful replica {repairData.ReplicaId} " +
                        $"on partition {repairData.PartitionId} " +
                        $"on node {repairData.NodeName}.";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.RestartReplicaAsync::Success",
                        statusSuccess,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                UpdateRepairHistory(repairData);
                ClearEntityHealthWarnings(repairData); 
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {
                string err =
                    $"Unable to restart stateful replica {repairData.ReplicaId} " +
                    $"on partition {repairData.PartitionId} " +
                    $"on node {repairData.NodeName}.{Environment.NewLine}" +
                    $"Exception Info:{Environment.NewLine}{e}";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "RepairExecutor.RestartReplicaAsync::Exception",
                        err,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                FabricHealerManager.RepairHistory.FailedRepairs++;
                return false;
            }

            return true;
        }

        public async Task<bool> RestartSystemServiceProcessAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            Process p = null;

            try
            {
                // FO/FHProxy provided the offending process id and (or, in the case of FHProxy) name in TelemetryData instance.
                if (repairData.ProcessId > 0)
                {
                    p = Process.GetProcessById((int)repairData.ProcessId);
                }
                else // We need to figure out the procId from the FO-supplied proc name.
                {
                    Process[] ps;

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && repairData.ProcessName.EndsWith(".dll"))
                    {
                        ps = GetDotnetProcessesByFirstArgument(repairData.ProcessName);
                    }
                    else
                    {
                        ps = Process.GetProcessesByName(repairData.ProcessName);
                    }

                    if (ps == null || ps.Length == 0)
                    {
                        string err =
                          $"Exception in RestartSystemServiceProcessAsync: Unable to restart process {repairData.ProcessName}. ps is null or empty";

                        await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Warning,
                                "RepairExecutor.RestartSystemServiceProcessAsync",
                                err,
                                cancellationToken,
                                repairData,
                                FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                        return false;
                    }

                    p = ps[0];
                }

                p?.Kill();
                UpdateRepairHistory(repairData);

                // Clear Warning from FO. If in fact the issue has not been solved, then FO will generate a new health report for the target and the game will be played again.
                ClearEntityHealthWarnings(repairData);
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException  || e is NotSupportedException || e is Win32Exception)
            {
                FabricHealerManager.RepairHistory.FailedRepairs++;
                string err =
                   $"Exception in RestartSystemServiceProcessAsync: Unable to restart process {repairData.ProcessName} " +
                   $"on node {repairData.NodeName}.{Environment.NewLine}" +
                   $"Exception Info:{Environment.NewLine}{e}";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "RepairExecutor.RestartSystemServiceProcessAsync",
                        err,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }
            catch (Exception e)
            {
                string err =
                   $"Unhandled Exception in RestartSystemServiceProcessAsync: Unable to restart process {repairData.ProcessName} " +
                   $"on node {repairData.NodeName}.{Environment.NewLine}" +
                   $"Exception Info:{Environment.NewLine}{e}";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "RepairExecutor.RestartSystemServiceProcessAsync",
                        err,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                FabricHealerManager.RepairHistory.FailedRepairs++;
                return false;
            }
            finally
            {
                p?.Dispose();
            }

            return true;
        }

        private Process[] GetDotnetProcessesByFirstArgument(string argument)
        {
            List<Process> result = new List<Process>();
            Process[] processes = Process.GetProcessesByName("dotnet");

            foreach (var p in processes)
            {
                try
                {
                    string cmdline = File.ReadAllText($"/proc/{p.Id}/cmdline");

                    // dotnet /mnt/sfroot/_App/__FabricSystem_App4294967295/US.Code.Current/FabricUS.dll 
                    if (cmdline.Contains("/mnt/sfroot/_App/"))
                    {
                        string bin = cmdline[(cmdline.LastIndexOf("/", StringComparison.Ordinal) + 1)..];

                        if (string.Equals(argument, bin, StringComparison.InvariantCulture))
                        {
                            result.Add(p);
                        }
                    }
                    else if (cmdline.Contains("Fabric"))
                    {
                        // dotnet FabricDCA.dll
                        string[] parts = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries);

                        if (parts.Length > 1 && string.Equals(argument, parts[1], StringComparison.Ordinal))
                        {
                            result.Add(p);
                        }
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    // It is possible that the process already exited.
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Removes a stateless instance.
        /// </summary>
        /// <param name="repairData">repairData instance.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns></returns>
        public async Task<bool> RemoveReplicaAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            string actionMessage = 
                $"Attempting to remove stateless instance {repairData.ReplicaId} " +
                $"on partition {repairData.PartitionId} on node {repairData.NodeName}.";

            await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.RemoveReplicaAsync::Start",
                    actionMessage,
                    cancellationToken,
                    repairData,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);
            try
            {
                // Make sure the replica still exists. \\

                var replicaList = await FabricHealerManager.FabricClientSingleton.QueryManager.GetReplicaListAsync(
                                            (Guid)repairData.PartitionId,
                                            repairData.ReplicaId,
                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                            cancellationToken);

                if (!replicaList.Any(r => r.ReplicaStatus == ServiceReplicaStatus.Ready))
                {
                    await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RepairExecutor.RemoveReplicaAsync",
                            $"Execution failure: Stateless instance {repairData.ReplicaId} " +
                            $"not found in partition {repairData.PartitionId}.",
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return false;
                }

                await FabricHealerManager.FabricClientSingleton.ServiceManager.RemoveReplicaAsync(
                        repairData.NodeName,
                        (Guid)repairData.PartitionId,
                        repairData.ReplicaId,
                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                        cancellationToken);

                string statusSuccess =
                    $"Successfully removed stateless instance {repairData.ReplicaId} " +
                    $"on partition {repairData.PartitionId} " +
                    $"on node {repairData.NodeName}.";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.RemoveReplicaAsync::Success",
                        statusSuccess,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                UpdateRepairHistory(repairData);
                ClearEntityHealthWarnings(repairData);
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException || e is OperationCanceledException)
            {
                string err =
                    $"Unable to remove stateless instance {repairData.ReplicaId} " +
                    $"on partition {repairData.PartitionId} " +
                    $"on node {repairData.NodeName}.{Environment.NewLine}" +
                    $"Exception Info:{Environment.NewLine}{e}";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "RepairExecutor.RemoveReplicaAsync::Exception",
                        err,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                FabricHealerManager.RepairHistory.FailedRepairs++;
                return false;
            }

            return true;
        }

        internal async Task<bool> DeleteFilesAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
           string actionMessage =
                $"Attempting to delete files in folder {(repairData.RepairPolicy as DiskRepairPolicy).FolderPath} " +
                $"on node {repairData.NodeName}.";

            await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.DeleteFilesAsync::Start",
                    actionMessage,
                    cancellationToken,
                    repairData,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

            string targetFolderPath = (repairData.RepairPolicy as DiskRepairPolicy).FolderPath;

            if (!Directory.Exists(targetFolderPath))
            {
                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.DeleteFilesAsync::DirectoryDoesNotExist",
                        $"The specified directory, {targetFolderPath}, does not exist.",
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }

            var dirInfo = new DirectoryInfo(targetFolderPath);
            FileSortOrder direction = (repairData.RepairPolicy as DiskRepairPolicy).FileAgeSortOrder;
            string searchPattern = (repairData.RepairPolicy as DiskRepairPolicy).FileSearchPattern;
            List<string> files = direction switch
            {
                FileSortOrder.Ascending => (from file in dirInfo.EnumerateFiles(searchPattern,
                        new EnumerationOptions
                        {
                            RecurseSubdirectories = (repairData.RepairPolicy as DiskRepairPolicy).RecurseSubdirectories
                        })
                    orderby file.LastWriteTimeUtc ascending
                    select file.FullName).Distinct().ToList(),
                FileSortOrder.Descending => (from file in dirInfo.EnumerateFiles(searchPattern,
                        new EnumerationOptions
                        {
                            RecurseSubdirectories = (repairData.RepairPolicy as DiskRepairPolicy).RecurseSubdirectories
                        })
                    orderby file.LastAccessTimeUtc descending
                    select file.FullName).Distinct().ToList(),
                _ => null
            };

            if (files != null)
            {
                int initialCount = files.Count;
                int deletedFiles = 0;
                long maxFiles = (repairData.RepairPolicy as DiskRepairPolicy).MaxNumberOfFilesToDelete;
  
                if (initialCount == 0)
                {
                    await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RepairExecutor.DeleteFilesAsync::NoFilesMatchSearchPattern",
                            $"No files match specified search pattern, {searchPattern}, in {targetFolderPath}. Nothing to do here.",
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return false;
                }

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (maxFiles > 0 && deletedFiles == maxFiles)
                    {
                        break;
                    }

                    try
                    {
                        File.Delete(file);
                        deletedFiles++;
                    }
                    catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException)
                    {
                        await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                "RepairExecutor.DeleteFilesAsync::HandledException",
                                $"Unable to delete {file}:{Environment.NewLine}{e}",
                                cancellationToken,
                                repairData,
                                FabricHealerManager.ConfigSettings.EnableVerboseLogging);
                    }
                }

                if (maxFiles > 0 && initialCount > maxFiles && deletedFiles < maxFiles)
                {
                    await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RepairExecutor.DeleteFilesAsync::IncompleteOperation",
                            $"Unable to delete specified number of files ({maxFiles}).",
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);
                    
                    FabricHealerManager.RepairHistory.FailedRepairs++;
                    return false;
                }
            
                if (maxFiles == 0 && deletedFiles < initialCount)
                {
                    await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RepairExecutor.DeleteFilesAsync::IncompleteOperation",
                            "Unable to delete all files.",
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    FabricHealerManager.RepairHistory.FailedRepairs++;
                    return false;
                }

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.DeleteFilesAsync::Success",
                        $"Successfully deleted {(maxFiles > 0 ? "up to " + maxFiles : "all")} files in {targetFolderPath}",
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                UpdateRepairHistory(repairData);
            }

            ClearEntityHealthWarnings(repairData);
            return true;
        }

        /// <summary>
        /// Clears existing health warnings for target repair entity. This should only be called after a repair operation succeeds.
        /// </summary>
        /// <param name="repairData">repairData instance.</param>
        private void ClearEntityHealthWarnings(TelemetryData repairData)
        {
            try
            {
                repairData.Description = $"{repairData.RepairPolicy.RepairAction} has completed successfully.";
                var healthInformation = new HealthInformation(repairData.Source, repairData.Property, HealthState.Ok)
                {
                    Description = JsonConvert.SerializeObject(repairData),
                    TimeToLive = TimeSpan.FromMinutes(5),
                    RemoveWhenExpired = true
                };

                var sendOptions = new HealthReportSendOptions { Immediate = true };

                switch (repairData.EntityType)
                {
                    case EntityType.Application when repairData.ApplicationName != null:

                        var appHealthReport = new ApplicationHealthReport(new Uri(repairData.ApplicationName), healthInformation);
                        FabricHealerManager.FabricClientSingleton.HealthManager.ReportHealth(appHealthReport, sendOptions);
                        break;

                    case EntityType.Service when repairData.ServiceName != null:

                        var serviceHealthReport = new ServiceHealthReport(new Uri(repairData.ServiceName), healthInformation);
                        FabricHealerManager.FabricClientSingleton.HealthManager.ReportHealth(serviceHealthReport, sendOptions);
                        break;

                    case EntityType.StatefulService when repairData.PartitionId != null && repairData.ReplicaId > 0:

                        var statefulServiceHealthReport = new StatefulServiceReplicaHealthReport((Guid)repairData.PartitionId, repairData.ReplicaId, healthInformation);
                        FabricHealerManager.FabricClientSingleton.HealthManager.ReportHealth(statefulServiceHealthReport, sendOptions);
                        break;

                    case EntityType.StatelessService when repairData.PartitionId != null && repairData.ReplicaId > 0:

                        var statelessServiceHealthReport = new StatelessServiceInstanceHealthReport((Guid)repairData.PartitionId, repairData.ReplicaId, healthInformation);
                        FabricHealerManager.FabricClientSingleton.HealthManager.ReportHealth(statelessServiceHealthReport, sendOptions);
                        break;

                    case EntityType.Partition when repairData.PartitionId != null:
                        var partitionHealthReport = new PartitionHealthReport((Guid)repairData.PartitionId, healthInformation);
                        FabricHealerManager.FabricClientSingleton.HealthManager.ReportHealth(partitionHealthReport, sendOptions);
                        break;

                    case EntityType.DeployedApplication when repairData != null && !string.IsNullOrWhiteSpace(repairData.NodeName):

                        var deployedApplicationHealthReport = new DeployedApplicationHealthReport(new Uri(repairData.ApplicationName), repairData.NodeName, healthInformation);
                        FabricHealerManager.FabricClientSingleton.HealthManager.ReportHealth(deployedApplicationHealthReport, sendOptions);
                        break;

                    case EntityType.Disk when !string.IsNullOrWhiteSpace(repairData.NodeName):
                    case EntityType.Machine when !string.IsNullOrWhiteSpace(repairData.NodeName):
                    case EntityType.Node when !string.IsNullOrWhiteSpace(repairData.NodeName):
                   
                        var nodeHealthReport = new NodeHealthReport(repairData.NodeName, healthInformation);
                        FabricHealerManager.FabricClientSingleton.HealthManager.ReportHealth(nodeHealthReport, sendOptions);
                        break;
                }
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {

            }
        }
    }
}