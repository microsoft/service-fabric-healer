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
using System.Reflection.Metadata.Ecma335;

namespace FabricHealer.Repair
{
    public class RepairExecutor
    {
        private const double MaxWaitTimeMinutesForNodeOperation = 60.0;
        private readonly FabricClient fabricClient;
        private readonly TelemetryUtilities telemetryUtilities;
        private readonly StatelessServiceContext serviceContext;

        private bool IsOneNodeCluster
        {
            get;
        }

        public RepairExecutor(FabricClient fabricClient, StatelessServiceContext context, CancellationToken token)
        {
            serviceContext = context;
            this.fabricClient = fabricClient;
            telemetryUtilities = new TelemetryUtilities(fabricClient, context);

            try
            {
                if (FabricHealerManager.ConfigSettings == null)
                {
                    return;
                }

                IsOneNodeCluster = 
                        this.fabricClient.QueryManager.GetNodeListAsync(null, FabricHealerManager.ConfigSettings.AsyncTimeout, token).GetAwaiter().GetResult().Count == 1;
            }
            catch (FabricException fe)
            {
                FabricHealerManager.RepairLogger.LogWarning($"Unable to determine cluster size:{Environment.NewLine}{fe}");
            }
        }

        public async Task<RestartDeployedCodePackageResult> RestartDeployedCodePackageAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken)
        {
            try
            {
                string actionMessage =
                    "Attempting to restart deployed code package for service " +
                    $"{repairConfiguration.ServiceName.OriginalString} " +
                    $"({repairConfiguration.ReplicaOrInstanceId}) on Node {repairConfiguration.NodeName}.";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RestartDeployedCodePackageAsync::Starting",
                        actionMessage,
                        cancellationToken,
                        repairConfiguration,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                PartitionSelector partitionSelector = PartitionSelector.PartitionIdOf(repairConfiguration.ServiceName, repairConfiguration.PartitionId);
                long replicaId = repairConfiguration.ReplicaOrInstanceId;
                Replica replica = null;

                // Verify target replica still exists.
                var replicaList = await fabricClient.QueryManager.GetReplicaListAsync(
                                            repairConfiguration.PartitionId,
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
                            $"Execution failure: Replica {repairConfiguration.ReplicaOrInstanceId} " +
                            $"not found in partition {repairConfiguration.PartitionId}.",
                            cancellationToken,
                            repairConfiguration,
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
                                                        fabricClient.FaultManager.RestartDeployedCodePackageAsync(
                                                            repairConfiguration.AppName,
                                                            replicaSelector,
                                                            completionMode, 
                                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                            cancellationToken), 
                                                        cancellationToken);

                if (restartCodePackageResult != null)
                {
                    UpdateRepairHistory(repairConfiguration);
                    ClearEntityHealthWarnings(repairConfiguration);

                    actionMessage =
                        "Successfully restarted deployed code package for service " +
                        $"{repairConfiguration.ServiceName.OriginalString} " +
                        $"({repairConfiguration.ReplicaOrInstanceId}) on Node {repairConfiguration.NodeName}.";

                    await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RestartDeployedCodePackageAsync::Success",
                            actionMessage,
                            cancellationToken,
                            repairConfiguration,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);
                }
                else
                {
                    actionMessage =
                       "Failed to restart deployed code package for service " +
                       $"{repairConfiguration.ServiceName.OriginalString} " +
                       $"({repairConfiguration.ReplicaOrInstanceId}) on Node {repairConfiguration.NodeName}.";

                    await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RestartDeployedCodePackageAsync::Failed",
                            actionMessage,
                            cancellationToken,
                            repairConfiguration,
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
                        repairConfiguration,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                FabricHealerManager.RepairHistory.FailedRepairs++;
                return null;
            }
        }

        private static void UpdateRepairHistory(RepairConfiguration repairConfiguration)
        {
            string repairName = Enum.GetName(typeof(RepairActionType), repairConfiguration.RepairPolicy.RepairAction);

            if (!FabricHealerManager.RepairHistory.Repairs.ContainsKey(repairName))
            {
                FabricHealerManager.RepairHistory.Repairs.Add(repairName, 1);
            }
            else
            {
                FabricHealerManager.RepairHistory.Repairs[repairName]++;
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
        /// <param name="repairConfiguration">Repair configuration</param>
        /// <param name="repairTask">The scheduled Repair Task</param>
        /// <param name="cancellationToken">Task cancellation token</param>
        /// <returns></returns>
        public async Task<bool> SafeRestartFabricNodeAsync(
                                    RepairConfiguration repairConfiguration,
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
                        repairConfiguration,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }

            var nodeQueryDesc = new NodeQueryDescription
            {
                MaxResults = 5,
            };

            NodeList nodeList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () => fabricClient.QueryManager.GetNodePagedListAsync(
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
                        repairConfiguration,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                FabricHealerManager.RepairLogger.LogInfo(info);
                return false;
            }

            ServiceDescription serviceDesc =
               await fabricClient.ServiceManager.GetServiceDescriptionAsync(serviceContext.ServiceName, FabricHealerManager.ConfigSettings.AsyncTimeout, cancellationToken);

            int instanceCount = (serviceDesc as StatelessServiceDescription).InstanceCount;

            if (instanceCount == -1)
            {
                bool isTargetNodeHostingFH = repairConfiguration.NodeName == serviceContext.NodeContext.NodeName;

                if (isTargetNodeHostingFH)
                {
                    return false;
                }
            }
          
            if (!nodeList.Any(n => n.NodeName == repairConfiguration.NodeName))
            {
                string info = $"Fabric node {repairConfiguration.NodeName} does not exist.";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.SafeRestartFabricNodeAsync::MissingNode",
                        info,
                        cancellationToken,
                        repairConfiguration,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);
            }

            var nodeInstanceId = nodeList.First(n => n.NodeName == repairConfiguration.NodeName).NodeInstanceId;
            var stopwatch = new Stopwatch();
            var maxWaitTimeout = TimeSpan.FromMinutes(MaxWaitTimeMinutesForNodeOperation);
            string actionMessage = $"Attempting to safely restart Fabric node {repairConfiguration.NodeName} with InstanceId {nodeInstanceId}.";
            await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.SafeRestartFabricNodeAsyncAttemptingRestart",
                    actionMessage,
                    cancellationToken,
                    repairConfiguration,
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
                                repairConfiguration,
                                FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                        return false;
                    }

                    await fabricClient.RepairManager.UpdateRepairExecutionStateAsync(repairTask, FabricHealerManager.ConfigSettings.AsyncTimeout, cancellationToken);

                    // Deactivate the node with intent to restart. Several health checks will 
                    // take place to ensure safe deactivation, which includes giving services a
                    // chance to gracefully shut down, should they override OnAbort/OnClose.
                    await fabricClient.ClusterManager.DeactivateNodeAsync(
                            repairConfiguration.NodeName,
                            NodeDeactivationIntent.Restart,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken);

                    stopwatch.Start();

                    // Wait for node to get into Disabled state.
                    while (stopwatch.Elapsed <= maxWaitTimeout)
                    {
                        var nodes = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                            () =>
                                            fabricClient.QueryManager.GetNodeListAsync(
                                                repairConfiguration.NodeName,
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

                    await fabricClient.RepairManager.UpdateRepairExecutionStateAsync(repairTask, FabricHealerManager.ConfigSettings.AsyncTimeout, cancellationToken);

                    actionMessage = $"In Step Restart Node.{Environment.NewLine}{repairTask.ExecutorData}";

                    await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RepairExecutor.SafeRestartFabricNodeAsyncAttemptingRestart::RestartStep",
                            actionMessage,
                            cancellationToken,
                            repairConfiguration,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    // Now, restart node.
                    _ = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                () =>
                                fabricClient.FaultManager.RestartNodeAsync(
                                    repairConfiguration.NodeName,
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
                                            fabricClient.QueryManager.GetNodeListAsync(
                                                repairConfiguration.NodeName,
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

                    await fabricClient.RepairManager.UpdateRepairExecutionStateAsync(repairTask, FabricHealerManager.ConfigSettings.AsyncTimeout, cancellationToken);

                    // Now, enable the node. 
                    await fabricClient.ClusterManager.ActivateNodeAsync(repairConfiguration.NodeName, FabricHealerManager.ConfigSettings.AsyncTimeout, cancellationToken);

                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);

                    var nodes = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () =>
                                        fabricClient.QueryManager.GetNodeListAsync(
                                            repairConfiguration.NodeName,
                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                            cancellationToken), 
                                        cancellationToken);

                    Node targetNode = nodes[0];

                    // Make sure activation request went through.
                    if (targetNode.NodeStatus == NodeStatus.Disabled && targetNode.HealthState == HealthState.Ok)
                    {
                        await fabricClient.ClusterManager.ActivateNodeAsync(repairConfiguration.NodeName, FabricHealerManager.ConfigSettings.AsyncTimeout, cancellationToken);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                    UpdateRepairHistory(repairConfiguration);
                    return true;
                }

                FabricHealerManager.RepairHistory.FailedRepairs++;
                return false;
            }
            catch (Exception e) when (e is FabricException || e is OperationCanceledException || e is TimeoutException)
            {
                string err = $"Handled Exception restarting Fabric node {repairConfiguration.NodeName}, NodeInstanceId {nodeInstanceId}:{e.GetType().Name}";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.SafeRestartFabricNodeAsync::HandledException",
                        err,
                        cancellationToken,
                        repairConfiguration,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                FabricHealerManager.RepairLogger.LogInfo(err);
                FabricHealerManager.RepairHistory.FailedRepairs++;
                return false;
            }
        }

        /// <summary>
        /// Restarts a stateful replica.
        /// </summary>
        /// <param name="repairConfiguration">RepairConfiguration instance.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns></returns>
        public async Task<bool> RestartReplicaAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken)
        {
            string actionMessage = $"Attempting to restart stateful replica {repairConfiguration.ReplicaOrInstanceId} " +
                                   $"on partition {repairConfiguration.PartitionId} on node {repairConfiguration.NodeName}.";
            
            await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.RestartReplicaAsync::Start",
                    actionMessage,
                    cancellationToken,
                    repairConfiguration,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);
            try
            {
                // Make sure the replica still exists. \\

                var replicaList = await fabricClient.QueryManager.GetReplicaListAsync(
                                            repairConfiguration.PartitionId,
                                            repairConfiguration.ReplicaOrInstanceId,
                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                            cancellationToken);

                if (!replicaList.Any(r => r.ReplicaStatus == ServiceReplicaStatus.Ready))
                {
                    await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RepairExecutor.RestartReplicaAsync",
                            $"Execution failure: Stateful replica {repairConfiguration.ReplicaOrInstanceId} " +
                            $"not found in partition {repairConfiguration.PartitionId}.",
                            cancellationToken,
                            repairConfiguration,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return false;
                }

                try
                {
                    await fabricClient.ServiceManager.RestartReplicaAsync(
                            repairConfiguration.NodeName,
                            repairConfiguration.PartitionId,
                            repairConfiguration.ReplicaOrInstanceId,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken);
                }
                catch (FabricException fe)
                {
                    // This would mean the stateful service replica is volatile (no persisted state), so we have to Remove it.
                    if (fe.ErrorCode == FabricErrorCode.InvalidReplicaOperation && fe.InnerException.Message == "0x80071C3A")
                    {
                        return await RemoveReplicaAsync(repairConfiguration, cancellationToken);                    
                    }
                }

                string statusSuccess =
                        $"Successfully restarted stateful replica {repairConfiguration.ReplicaOrInstanceId} " +
                        $"on partition {repairConfiguration.PartitionId} " +
                        $"on node {repairConfiguration.NodeName}.";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.RestartReplicaAsync::Success",
                        statusSuccess,
                        cancellationToken,
                        repairConfiguration,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                UpdateRepairHistory(repairConfiguration);
                ClearEntityHealthWarnings(repairConfiguration); 
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {
                string err =
                    $"Unable to restart stateful replica {repairConfiguration.ReplicaOrInstanceId} " +
                    $"on partition {repairConfiguration.PartitionId} " +
                    $"on node {repairConfiguration.NodeName}.{Environment.NewLine}" +
                    $"Exception Info:{Environment.NewLine}{e}";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "RepairExecutor.RestartReplicaAsync::Exception",
                        err,
                        cancellationToken,
                        repairConfiguration,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                FabricHealerManager.RepairHistory.FailedRepairs++;
                return false;
            }

            return true;
        }

        public async Task<bool> RestartSystemServiceProcessAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken)
        {
            Process p = null;

            try
            {
                // FO provided the offending process id in TelemetryData instance. Chances are good it will still be running.
                // If the process with this id is no longer running, then we can assume it makes no sense to try to restart it:
                // Just let the ArgumentException bubble out to the catch.
                if (repairConfiguration.ProcessId > -1)
                {
                    p = Process.GetProcessById(repairConfiguration.ProcessId);  
                }
                else // We need to figure out the procId from the FO-supplied proc name.
                {
                    Process[] ps;

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && repairConfiguration.SystemServiceProcessName.EndsWith(".dll"))
                    {
                        ps = GetDotnetProcessesByFirstArgument(repairConfiguration.SystemServiceProcessName);
                    }
                    else
                    {
                        ps = Process.GetProcessesByName(repairConfiguration.SystemServiceProcessName);
                    }

                    if (ps == null || ps.Length == 0)
                    {
                        return false;
                    }

                    p = ps[0];
                }

                p?.Kill(true);
                UpdateRepairHistory(repairConfiguration);

                // Clear Warning from FO. If in fact the issue has not been solved, then FO will generate a new health report for the target and the game will be played again.
                ClearEntityHealthWarnings(repairConfiguration);
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException  || e is NotSupportedException || e is Win32Exception)
            {
                FabricHealerManager.RepairHistory.FailedRepairs++;
                FabricHealerManager.RepairLogger.LogWarning(e.ToString());
                return false;
            }
            catch (Exception e)
            {
                string err =
                   $"Unhandled Exception in RestartSystemServiceProcessAsync: Unable to restart process {repairConfiguration.SystemServiceProcessName} " +
                   $"on node {repairConfiguration.NodeName}.{Environment.NewLine}" +
                   $"Exception Info:{Environment.NewLine}{e}";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "RepairExecutor.RestartSystemServiceProcessAsync",
                        err,
                        cancellationToken,
                        repairConfiguration,
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
        /// <param name="repairConfiguration">RepairConfiguration instance.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns></returns>
        public async Task<bool> RemoveReplicaAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken)
        {
            string actionMessage = 
                $"Attempting to remove stateless instance {repairConfiguration.ReplicaOrInstanceId} " +
                $"on partition {repairConfiguration.PartitionId} on node {repairConfiguration.NodeName}.";

            await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.RemoveReplicaAsync::Start",
                    actionMessage,
                    cancellationToken,
                    repairConfiguration,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);
            try
            {
                // Make sure the replica still exists. \\

                var replicaList = await fabricClient.QueryManager.GetReplicaListAsync(
                                            repairConfiguration.PartitionId,
                                            repairConfiguration.ReplicaOrInstanceId,
                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                            cancellationToken);

                if (!replicaList.Any(r => r.ReplicaStatus == ServiceReplicaStatus.Ready))
                {
                    await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RepairExecutor.RemoveReplicaAsync",
                            $"Execution failure: Stateless instance {repairConfiguration.ReplicaOrInstanceId} " +
                            $"not found in partition {repairConfiguration.PartitionId}.",
                            cancellationToken,
                            repairConfiguration,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return false;
                }

                await fabricClient.ServiceManager.RemoveReplicaAsync(
                        repairConfiguration.NodeName,
                        repairConfiguration.PartitionId,
                        repairConfiguration.ReplicaOrInstanceId,
                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                        cancellationToken);

                string statusSuccess =
                    $"Successfully removed stateless instance {repairConfiguration.ReplicaOrInstanceId} " +
                    $"on partition {repairConfiguration.PartitionId} " +
                    $"on node {repairConfiguration.NodeName}.";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.RemoveReplicaAsync::Success",
                        statusSuccess,
                        cancellationToken,
                        repairConfiguration,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                UpdateRepairHistory(repairConfiguration);
                ClearEntityHealthWarnings(repairConfiguration);
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException || e is OperationCanceledException)
            {
                string err =
                    $"Unable to remove stateless instance {repairConfiguration.ReplicaOrInstanceId} " +
                    $"on partition {repairConfiguration.PartitionId} " +
                    $"on node {repairConfiguration.NodeName}.{Environment.NewLine}" +
                    $"Exception Info:{Environment.NewLine}{e}";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "RepairExecutor.RemoveReplicaAsync::Exception",
                        err,
                        cancellationToken,
                        repairConfiguration,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                FabricHealerManager.RepairHistory.FailedRepairs++;
                return false;
            }

            return true;
        }

        internal async Task<bool> DeleteFilesAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken)
        {
           string actionMessage =
                $"Attempting to delete files in folder {(repairConfiguration.RepairPolicy as DiskRepairPolicy).FolderPath} " +
                $"on node {repairConfiguration.NodeName}.";

            await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.DeleteFilesAsync::Start",
                    actionMessage,
                    cancellationToken,
                    repairConfiguration,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

            string targetFolderPath = (repairConfiguration.RepairPolicy as DiskRepairPolicy).FolderPath;

            if (!Directory.Exists(targetFolderPath))
            {
                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.DeleteFilesAsync::DirectoryDoesNotExist",
                        $"The specified directory, {targetFolderPath}, does not exist.",
                        cancellationToken,
                        repairConfiguration,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }

            var dirInfo = new DirectoryInfo(targetFolderPath);
            FileSortOrder direction = (repairConfiguration.RepairPolicy as DiskRepairPolicy).FileAgeSortOrder;
            string searchPattern = (repairConfiguration.RepairPolicy as DiskRepairPolicy).FileSearchPattern;
            List<string> files = direction switch
            {
                FileSortOrder.Ascending => (from file in dirInfo.EnumerateFiles(searchPattern,
                        new EnumerationOptions
                        {
                            RecurseSubdirectories = (repairConfiguration.RepairPolicy as DiskRepairPolicy).RecurseSubdirectories
                        })
                    orderby file.LastWriteTimeUtc ascending
                    select file.FullName).Distinct().ToList(),
                FileSortOrder.Descending => (from file in dirInfo.EnumerateFiles(searchPattern,
                        new EnumerationOptions
                        {
                            RecurseSubdirectories = (repairConfiguration.RepairPolicy as DiskRepairPolicy).RecurseSubdirectories
                        })
                    orderby file.LastAccessTimeUtc descending
                    select file.FullName).Distinct().ToList(),
                _ => null
            };

            if (files != null)
            {
                int initialCount = files.Count;
                int deletedFiles = 0;
                long maxFiles = (repairConfiguration.RepairPolicy as DiskRepairPolicy).MaxNumberOfFilesToDelete;
  
                if (initialCount == 0)
                {
                    await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RepairExecutor.DeleteFilesAsync::NoFilesMatchSearchPattern",
                            $"No files match specified search pattern, {searchPattern}, in {targetFolderPath}. Nothing to do here.",
                            cancellationToken,
                            repairConfiguration,
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
                                repairConfiguration,
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
                            repairConfiguration,
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
                            repairConfiguration,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    FabricHealerManager.RepairHistory.FailedRepairs++;
                    return false;
                }

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.DeleteFilesAsync::Success",
                        $"Successfully deleted {(maxFiles > 0 ? "up to " + maxFiles : "all")} files in {targetFolderPath}",
                        cancellationToken,
                        repairConfiguration,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                UpdateRepairHistory(repairConfiguration);
            }

            ClearEntityHealthWarnings(repairConfiguration);
            return true;
        }

        /// <summary>
        /// Returns a machine name string, given a fabric node name.
        /// </summary>
        /// <param name="nodeName">Fabric node name</param>
        /// <param name="cancellationToken"></param>
        internal async Task<string> GetMachineHostNameFromFabricNodeNameAsync(string nodeName, CancellationToken cancellationToken)
        {
            try
            {
                var nodes = await fabricClient.QueryManager.GetNodeListAsync(
                                   nodeName,
                                   FabricHealerManager.ConfigSettings.AsyncTimeout,
                                   cancellationToken);

                Node targetNode = nodes.Count > 0 ? nodes[0] : null;

                if (targetNode == null)
                {
                    return null;
                }

                string ipOrDnsName = targetNode.IpAddressOrFQDN;
                var hostEntry = await Dns.GetHostEntryAsync(ipOrDnsName);
                var machineName = hostEntry.HostName;

                return machineName;
            }
            catch (Exception e) when (e is ArgumentException|| e is SocketException|| e is OperationCanceledException || e is TimeoutException)
            {
                FabricHealerManager.RepairLogger.LogWarning(
                    $"Unable to determine machine host name from Fabric node name {nodeName}:{Environment.NewLine}{e}");
            }

            return null;
        }

        /// <summary>
        /// Clears existing health warnings for target repair entity. This should only be called after a repair operation succeeds.
        /// </summary>
        /// <param name="repairConfiguration">RepairConfiguration instance.</param>
        private void ClearEntityHealthWarnings(RepairConfiguration repairConfiguration)
        {
            try
            {
                var telemetryData = new TelemetryData
                {
                    ApplicationName = repairConfiguration.AppName?.OriginalString,
                    ServiceName = repairConfiguration.ServiceName?.OriginalString,
                    Code = "FO000",
                    HealthState = HealthState.Ok,
                    Description = $"{(repairConfiguration.EventSourceId.Contains(RepairConstants.FabricSystemObserver) ? repairConfiguration.SystemServiceProcessName : repairConfiguration.RepairPolicy.RepairId)} repair has successfully completed.",
                    NodeName = repairConfiguration.NodeName,
                    NodeType = repairConfiguration.NodeType,
                    Source = RepairTaskEngine.FabricHealerExecutorName,
                    SystemServiceProcessName = $"{(repairConfiguration.EventSourceId.Contains(RepairConstants.FabricSystemObserver) ? repairConfiguration.SystemServiceProcessName : string.Empty)}",
                };

                var healthInformation = new HealthInformation(repairConfiguration.EventSourceId, repairConfiguration.EventProperty, HealthState.Ok)
                {
                    Description = JsonConvert.SerializeObject(telemetryData),
                    TimeToLive = TimeSpan.FromMinutes(5),
                    RemoveWhenExpired = true
                };

                var sendOptions = new HealthReportSendOptions { Immediate = false };

                switch (repairConfiguration.EntityType)
                {
                    case EntityType.Application when repairConfiguration.AppName != null:

                        var appHealthReport = new ApplicationHealthReport(repairConfiguration.AppName, healthInformation);
                        fabricClient.HealthManager.ReportHealth(appHealthReport, sendOptions);
                        break;

                    case EntityType.Service when repairConfiguration.ServiceName != null:

                        var serviceHealthReport = new ServiceHealthReport(repairConfiguration.ServiceName, healthInformation);
                        fabricClient.HealthManager.ReportHealth(serviceHealthReport, sendOptions);
                        break;

                    case EntityType.StatefulService when repairConfiguration.PartitionId != null && repairConfiguration.ReplicaOrInstanceId > 0:

                        var statefulServiceHealthReport = new StatefulServiceReplicaHealthReport(repairConfiguration.PartitionId, repairConfiguration.ReplicaOrInstanceId, healthInformation);
                        fabricClient.HealthManager.ReportHealth(statefulServiceHealthReport, sendOptions);
                        break;

                    case EntityType.StatelessService when repairConfiguration.PartitionId != null && repairConfiguration.ReplicaOrInstanceId > 0:

                        var statelessServiceHealthReport = new StatelessServiceInstanceHealthReport(repairConfiguration.PartitionId, repairConfiguration.ReplicaOrInstanceId, healthInformation);
                        fabricClient.HealthManager.ReportHealth(statelessServiceHealthReport, sendOptions);
                        break;

                    case EntityType.Partition when repairConfiguration.PartitionId != null:
                        var partitionHealthReport = new PartitionHealthReport(repairConfiguration.PartitionId, healthInformation);
                        fabricClient.HealthManager.ReportHealth(partitionHealthReport, sendOptions);
                        break;

                    case EntityType.DeployedApplication when repairConfiguration != null && !string.IsNullOrWhiteSpace(repairConfiguration.NodeName):

                        var deployedApplicationHealthReport = new DeployedApplicationHealthReport(repairConfiguration.AppName, repairConfiguration.NodeName, healthInformation);
                        fabricClient.HealthManager.ReportHealth(deployedApplicationHealthReport, sendOptions);
                        break;

                    case EntityType.Node when !string.IsNullOrWhiteSpace(repairConfiguration.NodeName):
                        var nodeHealthReport = new NodeHealthReport(repairConfiguration.NodeName, healthInformation);
                        fabricClient.HealthManager.ReportHealth(nodeHealthReport, sendOptions);
                        break;
                }
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {

            }
        }
    }
}