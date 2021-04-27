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
using System.Security;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.ComponentModel;

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
                        this.fabricClient.QueryManager.GetNodeListAsync(
                            null,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            token).GetAwaiter().GetResult().Count == 1;
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
                PartitionSelector partitionSelector = PartitionSelector.PartitionIdOf(repairConfiguration.ServiceName, repairConfiguration.PartitionId);
                long replicaId = repairConfiguration.ReplicaOrInstanceId;
                Replica replica = null;

                // Verify target replica still exists.
                var replicaList = await fabricClient.QueryManager.GetReplicaListAsync(
                                            repairConfiguration.PartitionId,
                                            replicaId,
                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                            cancellationToken).ConfigureAwait(false);
                
                if (replicaList.Any(r => r.ReplicaStatus == ServiceReplicaStatus.Ready))
                {
                    replica = replicaList.First(r => r.ReplicaStatus == ServiceReplicaStatus.Ready);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);

                    replicaList = await fabricClient.QueryManager.GetReplicaListAsync(
                                            repairConfiguration.PartitionId,
                                            null,
                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                            cancellationToken).ConfigureAwait(false);

                    if (replicaList.Count == 0)
                    {
                        await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                "RepairExecutor.RestartCodePackageAsync",
                                $"Execution failure: Replica {repairConfiguration.ReplicaOrInstanceId} not found in partition {repairConfiguration.PartitionId}.",
                                cancellationToken).ConfigureAwait(false);

                        return null;
                    }

                    replica = replicaList.First(r => r.ReplicaStatus == ServiceReplicaStatus.Ready);
                    replicaId = replica.Id;
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
                                                        cancellationToken).ConfigureAwait(true);

                if (restartCodePackageResult != null)
                {
                    await ClearHealthWarningsAsync(repairConfiguration, HealthScope.Application, cancellationToken, "AppObserver").ConfigureAwait(false);
                }

                return restartCodePackageResult;
            }
            catch (Exception e) when (e is FabricException  || e is InvalidOperationException  || e is OperationCanceledException || e is TimeoutException)
            {
                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "RepairExecutor.RestartCodePackageAsync",
                        $"Execution failure:{Environment.NewLine}{e}",
                        cancellationToken).ConfigureAwait(false);

                return null;
            }
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
        /// <param name="nodeName">Name of the target node</param>
        /// <param name="repairTask">The scheduled Repair Task</param>
        /// <param name="cancellationToken">Task cancellation token</param>
        /// <returns></returns>
        public async Task<bool> SafeRestartFabricNodeAsync(string nodeName, RepairTask repairTask, CancellationToken cancellationToken)
        {
            if (IsOneNodeCluster)
            {
                string info = "One node cluster detected. Aborting node restart operation.";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.SafeRestartFabricNodeAsync::NodeCount_1",
                        info,
                        cancellationToken).ConfigureAwait(false);

                FabricHealerManager.RepairLogger.LogInfo(info);

                return false;
            }

            bool isTargetNodeHostingFH = nodeName == serviceContext.NodeContext.NodeName;

            if (isTargetNodeHostingFH)
            {
                return false;
            }

            var nodes = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                   () =>
                                       fabricClient.QueryManager.GetNodeListAsync(
                                                                    nodeName,
                                                                    FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                    cancellationToken), 
                                    cancellationToken).ConfigureAwait(false);

            if (nodes.Count == 0)
            {
                string info = $"Target node not found: {nodeName}. Aborting node restart operation.";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.SafeRestartFabricNodeAsync::NodeCount0",
                        info,
                        cancellationToken).ConfigureAwait(false);

                FabricHealerManager.RepairLogger.LogInfo(info);

                return false;
            }

            var allnodes = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                      () =>
                                          fabricClient.QueryManager.GetNodeListAsync(
                                                                     null,
                                                                     FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                     cancellationToken),
                                       cancellationToken).ConfigureAwait(false);
   
            if (allnodes.Count < 3)
            {
                string info = $"Unsupported repair for a {nodes.Count} node cluster. Aborting fabric node restart operation.";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.SafeRestartFabricNodeAsync::NodeCount",
                        info,
                        cancellationToken).ConfigureAwait(false);

                FabricHealerManager.RepairLogger.LogInfo(info);

                return false;
            }

            var nodeInstanceId = nodes[0].NodeInstanceId;
            var stopwatch = new Stopwatch();
            var maxWaitTimeout = TimeSpan.FromMinutes(MaxWaitTimeMinutesForNodeOperation);

            string actionMessage = $"Attempting to safely restart Fabric node {nodeName} with InstanceId {nodeInstanceId}.";

            FabricHealerManager.RepairLogger.LogInfo(actionMessage);

            await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.SafeRestartFabricNodeAsyncAttemptingRestart",
                    actionMessage,
                    cancellationToken).ConfigureAwait(false);
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
                                cancellationToken).ConfigureAwait(false);

                        return false;
                    }

                    await fabricClient.RepairManager.UpdateRepairExecutionStateAsync(
                            repairTask,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken).ConfigureAwait(false);

                    // Deactivate the node with intent to restart. Several health checks will 
                    // take place to ensure safe deactivation, which includes giving services a
                    // chance to gracefully shut down, should they override OnAbort/OnClose.
                    await fabricClient.ClusterManager.DeactivateNodeAsync(
                                                        nodeName,
                                                        NodeDeactivationIntent.Restart,
                                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                        cancellationToken).ConfigureAwait(false);

                    stopwatch.Start();

                    // Wait for node to get into Disabled state.
                    while (stopwatch.Elapsed <= maxWaitTimeout)
                    {
                        var nodeList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                  () =>
                                                      fabricClient.QueryManager.GetNodeListAsync(
                                                                                 nodeName,
                                                                                 FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                                 cancellationToken),
                                                  cancellationToken).ConfigureAwait(false);

                        if (nodeList == null || nodeList.Count == 0)
                        {
                            break;
                        }

                        Node targetNode = nodeList[0];

                        // exit loop, this is the state we're looking for.
                        if (targetNode.NodeStatus == NodeStatus.Disabled)
                        {
                            break;
                        }

                        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
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

                    await fabricClient.RepairManager.UpdateRepairExecutionStateAsync(
                                                        repairTask,
                                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                        cancellationToken).ConfigureAwait(false);

                    actionMessage = $"In Step Restart Node.{Environment.NewLine}{repairTask.ExecutorData}";

                     await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RepairExecutor.SafeRestartFabricNodeAsyncAttemptingRestart::RestartStep",
                            actionMessage,
                            cancellationToken).ConfigureAwait(false);

                    // Now, restart node.
                    _ = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                   () =>
                                       fabricClient.FaultManager.RestartNodeAsync(
                                                                   nodeName,
                                                                   nodes[0].NodeInstanceId,
                                                                   FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                   cancellationToken),
                                    cancellationToken).ConfigureAwait(false);
                    

                    stopwatch.Start();

                    // Wait for Disabled/OK
                    while (stopwatch.Elapsed <= maxWaitTimeout)
                    {
                        var nodeList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                  () =>
                                                      fabricClient.QueryManager.GetNodeListAsync(
                                                                                 nodeName,
                                                                                 FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                                 cancellationToken),
                                                  cancellationToken).ConfigureAwait(false);

                        Node targetNode = nodeList[0];

                        // Node is ready to be enabled.
                        if (targetNode.NodeStatus == NodeStatus.Disabled && targetNode.HealthState == HealthState.Ok)
                        {
                            break;
                        }

                        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
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

                    await fabricClient.RepairManager.UpdateRepairExecutionStateAsync(
                                                        repairTask,
                                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                        cancellationToken).ConfigureAwait(false);

                    // Now, enable the node. 
                    await fabricClient.ClusterManager.ActivateNodeAsync(
                                                        nodeName,
                                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                        cancellationToken).ConfigureAwait(false);

                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

                    var nodeList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                              () =>
                                                  fabricClient.QueryManager.GetNodeListAsync(
                                                                             nodeName,
                                                                             FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                             cancellationToken),
                                              cancellationToken).ConfigureAwait(false);

                    Node targetNode = nodeList[0];

                    // Make sure activation request went through.
                    if (targetNode.NodeStatus == NodeStatus.Disabled && targetNode.HealthState == HealthState.Ok)
                    {
                        await fabricClient.ClusterManager.ActivateNodeAsync(
                                                            nodeName,
                                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                            cancellationToken).ConfigureAwait(false);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

                    return true;
                }

                return false;
            }
            catch (Exception e) when (e is FabricException || e is OperationCanceledException || e is TimeoutException)
            {
                string err = $"Handled Exception restarting Fabric node {nodeName}, NodeInstanceId {nodeInstanceId}:{e.GetType().Name}";

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.SafeRestartFabricNodeAsync::HandledException",
                        err,
                        cancellationToken).ConfigureAwait(false);

                FabricHealerManager.RepairLogger.LogInfo(err);

                return false;
            }
        }

        public async Task<RestartReplicaResult> RestartReplicaAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken)
        {
            string actionMessage = $"Attempting to restart replica {repairConfiguration.ReplicaOrInstanceId} " +
                                   $"on partition {repairConfiguration.PartitionId} on node {repairConfiguration.NodeName}.";

            FabricHealerManager.RepairLogger.LogInfo(actionMessage);

            await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.RestartCodePackageAsync::Start",
                    actionMessage,
                    cancellationToken,
                    repairConfiguration).ConfigureAwait(false);

            RestartReplicaResult replicaResult;

            try
            {
                PartitionSelector partitionSelector = PartitionSelector.PartitionIdOf(repairConfiguration.ServiceName, repairConfiguration.PartitionId);
                ReplicaSelector replicaSelector = ReplicaSelector.ReplicaIdOf(partitionSelector, repairConfiguration.ReplicaOrInstanceId);

                replicaResult = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                           () =>
                                               fabricClient.FaultManager.RestartReplicaAsync(
                                                                            replicaSelector,
                                                                            CompletionMode.DoNotVerify,
                                                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                            cancellationToken),
                                            cancellationToken).ConfigureAwait(false);

                string statusSuccess =
                    $"Successfully restarted replica {repairConfiguration.ReplicaOrInstanceId} " +
                    $"on partition {repairConfiguration.PartitionId} " +
                    $"on node {repairConfiguration.NodeName}.";

                FabricHealerManager.RepairLogger.LogInfo(statusSuccess);

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.RestartReplicaAsync::Success",
                        statusSuccess,
                        cancellationToken,
                        repairConfiguration).ConfigureAwait(false);
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException || e is OperationCanceledException)
            {
                string err =
                    $"Unable to restart replica {repairConfiguration.ReplicaOrInstanceId} " +
                    $"on partition {repairConfiguration.PartitionId} " +
                    $"on node {repairConfiguration.NodeName}.{Environment.NewLine}" +
                    $"Exception Info:{Environment.NewLine}{e}";

                FabricHealerManager.RepairLogger.LogWarning(err);

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                       LogLevel.Warning,
                       "RepairExecutor.RestartReplicaAsync::Exception",
                       err,
                       cancellationToken,
                       repairConfiguration).ConfigureAwait(false);

                return null;
            }

            if (replicaResult != null)
            {
                await ClearHealthWarningsAsync(repairConfiguration, HealthScope.Application, cancellationToken).ConfigureAwait(false);
            }

            return replicaResult;
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

                // Clear Warning from FO. If in fact the issue has not been solved, then FO will generate a new health report for the target and the game will be played again.
                await ClearHealthWarningsAsync(repairConfiguration, HealthScope.Application, cancellationToken, "FabricSystemObserver").ConfigureAwait(false);
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException  || e is NotSupportedException || e is Win32Exception)
            {
                return false;
            }
            catch (Exception e)
            {
                string err =
                   $"Unhandled Exception in RestartSystemServiceProcessAsync: Unable to restart process {repairConfiguration.SystemServiceProcessName} " +
                   $"on node {repairConfiguration.NodeName}.{Environment.NewLine}" +
                   $"Exception Info:{Environment.NewLine}{e}";

                FabricHealerManager.RepairLogger.LogWarning(err);

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                       LogLevel.Warning,
                       "RepairExecutor.RestartSystemServiceProcessAsync",
                       err,
                       cancellationToken,
                       repairConfiguration);

                // fix the bug..
                throw;
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

        public async Task<RemoveReplicaResult> RemoveReplicaAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken)
        {
            string actionMessage = 
                $"Attempting to remove replica {repairConfiguration.ReplicaOrInstanceId} " +
                $"on partition {repairConfiguration.PartitionId} on node {repairConfiguration.NodeName}.";

            FabricHealerManager.RepairLogger.LogInfo(actionMessage);

            await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.RemoveReplicaAsync::Start",
                    actionMessage,
                    cancellationToken,
                    repairConfiguration).ConfigureAwait(false);

            RemoveReplicaResult replicaResult;

            try
            {
                replicaResult = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                           () =>
                                               fabricClient.FaultManager.RemoveReplicaAsync(
                                                                            repairConfiguration.NodeName,
                                                                            repairConfiguration.PartitionId,
                                                                            repairConfiguration.ReplicaOrInstanceId,
                                                                            CompletionMode.DoNotVerify,
                                                                            false,
                                                                            FabricHealerManager.ConfigSettings.AsyncTimeout.TotalSeconds,
                                                                            cancellationToken),
                                            cancellationToken).ConfigureAwait(false);

                string statusSuccess =
                    $"Successfully removed replica {repairConfiguration.ReplicaOrInstanceId} " +
                    $"on partition {repairConfiguration.PartitionId} " +
                    $"on node {repairConfiguration.NodeName}.";

                FabricHealerManager.RepairLogger.LogInfo(statusSuccess);

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.RemoveReplicaAsync::Success",
                        statusSuccess,
                        cancellationToken,
                        repairConfiguration).ConfigureAwait(false);
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException || e is OperationCanceledException)
            {
                string err =
                    $"Unable to remove replica {repairConfiguration.ReplicaOrInstanceId} " +
                    $"on partition {repairConfiguration.PartitionId} " +
                    $"on node {repairConfiguration.NodeName}.{Environment.NewLine}" +
                    $"Exception Info:{Environment.NewLine}{e}";

                FabricHealerManager.RepairLogger.LogWarning(err);

                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                       LogLevel.Warning,
                       "RepairExecutor.RemoveReplicaAsync::Exception",
                       err,
                       cancellationToken,
                       repairConfiguration).ConfigureAwait(false);

                return null;
            }

            if (replicaResult != null)
            {
                await ClearHealthWarningsAsync(repairConfiguration, HealthScope.Application, cancellationToken).ConfigureAwait(false);
            }

            return replicaResult;
        }

        internal async Task<bool> DeleteFilesAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken)
        {
           string actionMessage =
                $"Attempting to delete files in folder {((DiskRepairPolicy)repairConfiguration.RepairPolicy).FolderPath} " +
                $"on node {repairConfiguration.NodeName}.";

            FabricHealerManager.RepairLogger.LogInfo(actionMessage);

            await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.DeleteFilesAsync::Start",
                    actionMessage,
                    cancellationToken,
                    repairConfiguration).ConfigureAwait(false);

            string targetFolderPath = ((DiskRepairPolicy)repairConfiguration.RepairPolicy).FolderPath;

            if (!Directory.Exists(targetFolderPath))
            {
                return false;
            }

            var dirInfo = new DirectoryInfo(targetFolderPath);
            FileSortOrder direction = ((DiskRepairPolicy)repairConfiguration.RepairPolicy).FileAgeSortOrder;
            List<string> files = null;

            if (direction == FileSortOrder.Ascending)
            {
                files = (from file in dirInfo.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = ((DiskRepairPolicy)repairConfiguration.RepairPolicy).RecurseSubdirectories })
                         orderby file.LastWriteTimeUtc ascending
                         select file.FullName).Distinct().ToList();
            }
            else if (direction == FileSortOrder.Descending)
            {
                files = (from file in dirInfo.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = ((DiskRepairPolicy)repairConfiguration.RepairPolicy).RecurseSubdirectories })
                         orderby file.LastAccessTimeUtc descending
                         select file.FullName).Distinct().ToList();
            }
   
            int initialCount = files.Count;
            int deletedFiles = 0;
            long maxFiles = ((DiskRepairPolicy)repairConfiguration.RepairPolicy).MaxNumberOfFilesToDelete;
  
            if (initialCount == 0)
            {
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
                catch (Exception e) when (e is IOException || e is SecurityException)
                {
                    await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RepairExecutor.DeleteFilesAsync::HandledException",
                            $"Unable to delete {file}:{Environment.NewLine}{e}",
                            cancellationToken,
                            repairConfiguration).ConfigureAwait(false);
                }
            }

            if (maxFiles > 0 && initialCount > maxFiles && deletedFiles < maxFiles)
            {
                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.DeleteFilesAsync::IncompleteOperation",
                        $"Unable to delete specified number of files ({maxFiles}).",
                        cancellationToken,
                        repairConfiguration).ConfigureAwait(false);

                return false;
            }
            
            if (maxFiles == 0 && deletedFiles < initialCount)
            {
                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.DeleteFilesAsync::IncompleteOperation",
                        "Unable to delete all files.",
                        cancellationToken,
                        repairConfiguration).ConfigureAwait(false); 

                return false;
            }

            await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.DeleteFilesAsync::Success",
                    $"Successfully deleted {(maxFiles > 0 ? "up to " + maxFiles : "all")} files in {targetFolderPath}",
                    cancellationToken,
                        repairConfiguration).ConfigureAwait(false);

            await ClearHealthWarningsAsync(repairConfiguration, HealthScope.Node, cancellationToken, "DiskObserver").ConfigureAwait(false);

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
                                   cancellationToken).ConfigureAwait(true);

                Node targetNode = nodes.Count > 0 ? nodes[0] : null;

                if (targetNode == null)
                {
                    return null;
                }

                string ipOrDnsName = targetNode.IpAddressOrFQDN;
                var hostEntry = await Dns.GetHostEntryAsync(ipOrDnsName).ConfigureAwait(false);
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
        /// <param name="repairConfiguration">RepairConfiguration instance used for repair.</param>
        /// <param name="healthScope">Repair target health scope (FO, by default, produces 2 types of reports: Application and Node)</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <param name="source">Optional: The name of the source Observer (AppObserver, DiskObserver, etc...)</param>
        /// <returns></returns>
        private async Task ClearHealthWarningsAsync(RepairConfiguration repairConfiguration, HealthScope healthScope, CancellationToken cancellationToken, string source = null)
        {
            try
            {
                if (healthScope == HealthScope.Application)
                {
                    var appHealth = await fabricClient.HealthManager.GetApplicationHealthAsync(
                                                                      repairConfiguration.AppName,
                                                                      FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                      cancellationToken).ConfigureAwait(false);

                    var unhealthyFOAppEvents = appHealth.HealthEvents?.Where(
                                                  s => s.HealthInformation.SourceId.Contains(source ?? "Observer")
                                                    && (s.HealthInformation.HealthState == HealthState.Error || s.HealthInformation.HealthState == HealthState.Warning)
                                                    && JsonSerializationUtility.TryDeserialize(s.HealthInformation.Description, out TelemetryData foHealthData)
                                                    && foHealthData.ApplicationName == repairConfiguration.AppName.OriginalString
                                                    && (source is "FabricSystemObserver" ? foHealthData.SystemServiceProcessName == repairConfiguration.SystemServiceProcessName : foHealthData.ServiceName == repairConfiguration.ServiceName.OriginalString));

                    var telemetryData = new TelemetryData
                    {
                        ApplicationName = repairConfiguration.AppName?.OriginalString,
                        ServiceName = repairConfiguration.ServiceName?.OriginalString,
                        Code = "FO000",
                        HealthState = "Ok",
                        Description = $"{(source is "FabricSystemObserver" ? repairConfiguration.SystemServiceProcessName : repairConfiguration.ServiceName.OriginalString)} has been repaired.",
                        NodeName = repairConfiguration.NodeName,
                        NodeType = repairConfiguration.NodeType,
                        Source = RepairTaskEngine.FabricHealerExecutorName,
                        SystemServiceProcessName = $"{(source is "FabricSystemObserver" ? repairConfiguration.SystemServiceProcessName : string.Empty)}",
                    };

                    if (unhealthyFOAppEvents != null)
                    {
                        foreach (var evt in unhealthyFOAppEvents)
                        {
                            if (repairConfiguration.ServiceName is { })
                            {
                                var healthInfo = new HealthInformation(evt.HealthInformation.SourceId, evt.HealthInformation.Property, HealthState.Ok)
                                {
                                    Description = JsonSerializationUtility.TrySerialize(telemetryData, out string data)
                                        ? data
                                        : $"{(source is "FabricSystemObserver" ? repairConfiguration.SystemServiceProcessName : repairConfiguration.ServiceName.OriginalString)} has been repaired.",
                                    TimeToLive = TimeSpan.FromMinutes(5),
                                    RemoveWhenExpired = true,
                                };

                                var healthReport = new ApplicationHealthReport(repairConfiguration.AppName, healthInfo);
                                fabricClient.HealthManager.ReportHealth(healthReport,
                                    new HealthReportSendOptions {Immediate = true});
                            }

                            await Task.Delay(250, cancellationToken);
                        }
                    }
                }
                else
                {
                    var nodeHealth = await fabricClient.HealthManager.GetNodeHealthAsync(
                                                                        repairConfiguration.NodeName,
                                                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                        cancellationToken).ConfigureAwait(false);

                    var unhealthyFONodeEvents = nodeHealth.HealthEvents?.Where(
                                                  s => s.HealthInformation.SourceId.Contains(source ?? "Observer")
                                                    && (s.HealthInformation.HealthState == HealthState.Error || s.HealthInformation.HealthState == HealthState.Warning)
                                                    && JsonSerializationUtility.TryDeserialize(s.HealthInformation.Description, out TelemetryData foHealthData)
                                                    && foHealthData?.NodeName == repairConfiguration.NodeName
                                                    && foHealthData?.Code == repairConfiguration.FOErrorCode);

                    var telemetryData = new TelemetryData
                    {
                        Code = "FO000",
                        HealthState = "Ok",
                        Description = $"{repairConfiguration.NodeName} has been repaired.",
                        NodeName = repairConfiguration.NodeName,
                        NodeType = repairConfiguration.NodeType,
                        Source = RepairTaskEngine.FabricHealerExecutorName,
                    };

                    foreach (var evt in unhealthyFONodeEvents)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var healthInfo = new HealthInformation(evt.HealthInformation.SourceId, evt.HealthInformation.Property, HealthState.Ok)
                        {
                            Description = JsonSerializationUtility.TrySerialize(telemetryData, out string data) ? data : $"{repairConfiguration.NodeName} has been repaired.",
                            TimeToLive = TimeSpan.FromMinutes(5),
                            RemoveWhenExpired = true,
                        };

                        var healthReport = new NodeHealthReport(repairConfiguration.NodeName, healthInfo);
                        fabricClient.HealthManager.ReportHealth(healthReport, new HealthReportSendOptions { Immediate = true });

                        await Task.Delay(250, cancellationToken);
                    }
                }
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {

            }
        }
    }
}