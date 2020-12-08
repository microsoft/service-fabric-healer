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

namespace FabricHealer.Repair
{
    public class RepairExecutor
    {
        private const double MaxWaitTimeMinutesForNodeOperation = 60.0;
        private readonly FabricClient fabricClient;
        private readonly TelemetryUtilities telemetryUtilities;
        private readonly StatelessServiceContext serviceContext;
        private readonly RepairTaskEngine repairTaskEngine;

        public bool IsOneNodeCluster
        {
            get;
        }

        public RepairExecutor(
            FabricClient fabricClient,
            StatelessServiceContext context,
            CancellationToken token)
        {
            this.serviceContext = context;
            this.fabricClient = fabricClient;
            this.telemetryUtilities = new TelemetryUtilities(fabricClient, context);
            this.repairTaskEngine = new RepairTaskEngine(fabricClient);

            try
            {
                IsOneNodeCluster = 
                        this.fabricClient.QueryManager.GetNodeListAsync(
                            null,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            token).GetAwaiter().GetResult().Count == 1;
            }
            catch (FabricException fe)
            {
                FabricHealerManager.RepairLogger.LogWarning(
                    $"Unable to determine cluster size:{Environment.NewLine}{fe}");
            }
        }

        public async Task<RestartDeployedCodePackageResult> RestartCodePackageAsync(
            Uri appName,
            Guid partitionId,
            long replicaId,
            Uri serviceName,
            CancellationToken cancellationToken)
        {
            try
            {
                PartitionSelector partitionSelector = PartitionSelector.PartitionIdOf(serviceName, partitionId);

                // Verify target replica still exists.
                var replicaList = await fabricClient.QueryManager.GetReplicaListAsync(
                                            partitionId,
                                            replicaId,
                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                            cancellationToken).ConfigureAwait(false);
                if (replicaList.Count == 0)
                {
                    await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                       LogLevel.Info,
                       "RepairExecutor.RestartCodePackageAsync",
                       $"Execution failure: Replica {replicaId} not found in partition {partitionId}.",
                       cancellationToken).ConfigureAwait(false);

                    return null;
                }

                ReplicaSelector replicaSelector = ReplicaSelector.ReplicaIdOf(partitionSelector, replicaId);

                var restartCodePackageResult = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                    () =>
                        fabricClient.FaultManager.RestartDeployedCodePackageAsync(
                            appName,
                            replicaSelector,
                            CompletionMode.DoNotVerify, // There is a bug with Verify for Stateless services...
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken),
                    cancellationToken).ConfigureAwait(true);

                return restartCodePackageResult;
            }
            catch (Exception ex) 
                when (ex is FabricException 
                || ex is InvalidOperationException 
                || ex is OperationCanceledException 
                || ex is TimeoutException)
            {
                await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "RepairExecutor.RestartCodePackageAsync",
                        $"Execution failure: {ex}.",
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
        public async Task<bool> SafeRestartFabricNodeAsync(
            string nodeName,
            RepairTask repairTask,
            CancellationToken cancellationToken)
        {
            bool isTargetNodeHostingFH = nodeName == this.serviceContext.NodeContext.NodeName;

            if (isTargetNodeHostingFH)
            {
                return false;
            }

            var nodes = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                    () =>
                        this.fabricClient.QueryManager.GetNodeListAsync(
                            nodeName,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken), 
                    cancellationToken).ConfigureAwait(false);

            if (nodes.Count == 0)
            {
                string info =
                    $"Target node not found: {nodeName}. " +
                    $"Aborting node restart operation.";

                await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.SafeRestartFabricNodeAsync::NodeCount0",
                    info,
                    cancellationToken).ConfigureAwait(false);

                FabricHealerManager.RepairLogger.LogInfo(info);

                return false;
            }

            var allnodes = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                    () =>
                        this.fabricClient.QueryManager.GetNodeListAsync(
                            null,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken),
                    cancellationToken).ConfigureAwait(false);
   
            if (allnodes.Count < 3)
            {
                string info =
                    $"Unsupported repair for a {nodes.Count} node cluster. " +
                    $"Aborting fabric node restart operation.";

                await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
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

            string actionMessage = "Attempting to safely restart Fabric node " +
                $"{nodeName} with InstanceId {nodeInstanceId}.";

            FabricHealerManager.RepairLogger.LogInfo(actionMessage);

            await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.SafeRestartFabricNodeAsyncAttemptingRestart",
                    actionMessage,
                    cancellationToken).ConfigureAwait(false);
            try
            {
                if (!SerializationUtility.TryDeserialize(repairTask.ExecutorData, out RepairExecutorData executorData))
                {
                    return false;
                }

                if (executorData.LatestRepairStep == FabricNodeRepairStep.Scheduled)
                {
                    executorData.LatestRepairStep = FabricNodeRepairStep.Deactivate;

                    if (SerializationUtility.TrySerialize(executorData, out string exData))
                    {
                        repairTask.ExecutorData = exData;
                    }
                    else
                    {
                        actionMessage = "Step = Deactivate => Did not successfully serialize executordata.";

                        await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
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
                    await this.fabricClient.ClusterManager.DeactivateNodeAsync(
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
                                this.fabricClient.QueryManager.GetNodeListAsync(
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

                    if (SerializationUtility.TrySerialize(executorData, out string exData))
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

                     await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                         LogLevel.Info,
                         "RepairExecutor.SafeRestartFabricNodeAsyncAttemptingRestart::RestartStep",
                         actionMessage,
                         cancellationToken).ConfigureAwait(false);

                    // Now, restart node.
                    _ = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                            () =>
                                this.fabricClient.FaultManager.RestartNodeAsync(
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
                                this.fabricClient.QueryManager.GetNodeListAsync(
                                    nodeName,
                                    FabricHealerManager.ConfigSettings.AsyncTimeout,
                                    cancellationToken),
                            cancellationToken).ConfigureAwait(false);

                        Node targetNode = nodeList[0];

                        // Node is ready to be enabled.
                        if (targetNode.NodeStatus == NodeStatus.Disabled
                            && targetNode.HealthState == HealthState.Ok)
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

                    if (SerializationUtility.TrySerialize(executorData, out string exData))
                    {
                        repairTask.ExecutorData = exData;
                    }
                    else
                    {
                        return false;
                    }

                    await this.fabricClient.RepairManager.UpdateRepairExecutionStateAsync(
                                repairTask,
                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                cancellationToken).ConfigureAwait(false);

                    // Now, enable the node. 
                    await this.fabricClient.ClusterManager.ActivateNodeAsync(
                                nodeName,
                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                cancellationToken).ConfigureAwait(false);

                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

                    var nodeList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                            () =>
                                this.fabricClient.QueryManager.GetNodeListAsync(
                                    nodeName,
                                    FabricHealerManager.ConfigSettings.AsyncTimeout,
                                    cancellationToken),
                            cancellationToken).ConfigureAwait(false);

                    Node targetNode = nodeList[0];

                    // Make sure activation request went through.
                    if (targetNode.NodeStatus == NodeStatus.Disabled
                        && targetNode.HealthState == HealthState.Ok)
                    {
                        await this.fabricClient.ClusterManager.ActivateNodeAsync(
                                nodeName,
                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                cancellationToken).ConfigureAwait(false);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

                    return true;
                }

                return false;
            }
            catch (Exception e) 
                //when (e is FabricException || e is OperationCanceledException || e is TimeoutException)
            {
                string err =
                    $"Error restarting Fabric node {nodeName}, " +
                    $"NodeInstanceId {nodeInstanceId}:{Environment.NewLine}{e}";

                await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.SafeRestartFabricNodeAsync::HandledException",
                    err,
                    cancellationToken).ConfigureAwait(false);

                FabricHealerManager.RepairLogger.LogError(err);

                return false;
            }
        }

        public async Task<RestartReplicaResult> RestartReplicaAsync(
            RepairConfiguration repairConfiguration,
            CancellationToken cancellationToken)
        {
            string actionMessage = $"Attempting to restart replica {repairConfiguration.ReplicaOrInstanceId} " +
                                   $"on partition {repairConfiguration.PartitionId} on node {repairConfiguration.NodeName}.";

            FabricHealerManager.RepairLogger.LogInfo(actionMessage);

            await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.RestartCodePackageAsync",
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
                            this.fabricClient.FaultManager.RestartReplicaAsync(
                                replicaSelector,
                                CompletionMode.Verify,
                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                cancellationToken),
                        cancellationToken).ConfigureAwait(false);

                string statusSuccess =
                    $"Successfully restarted replica {repairConfiguration.ReplicaOrInstanceId} " +
                    $"on partition {repairConfiguration.PartitionId} " +
                    $"on node {repairConfiguration.NodeName}.";

                FabricHealerManager.RepairLogger.LogInfo(statusSuccess);

                await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.RestartReplicaAsync",
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

                await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                       LogLevel.Warning,
                       "RepairExecutor.RestartReplicaAsync",
                       err,
                       cancellationToken,
                       repairConfiguration).ConfigureAwait(false);

                return null;
            }

            return replicaResult;
        }

        public async Task<RemoveReplicaResult> RemoveReplicaAsync(
            RepairConfiguration repairConfiguration,
            CancellationToken cancellationToken)
        {
            string actionMessage = 
                $"Attempting to remove replica {repairConfiguration.ReplicaOrInstanceId} " +
                $"on partition {repairConfiguration.PartitionId} on node {repairConfiguration.NodeName}.";

            FabricHealerManager.RepairLogger.LogInfo(actionMessage);

            await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.RemoveCodePackageAsync",
                    actionMessage,
                    cancellationToken,
                    repairConfiguration).ConfigureAwait(false);

            RemoveReplicaResult replicaResult;

            try
            {
                replicaResult = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                        () =>
                            this.fabricClient.FaultManager.RemoveReplicaAsync(
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

                await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.RemoveReplicaAsync",
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

                await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                       LogLevel.Warning,
                       "RepairExecutor.RemoveReplicaAsync",
                       err,
                       cancellationToken,
                       repairConfiguration).ConfigureAwait(false);

                return null;
            }

            return replicaResult;
        }

        internal async Task<bool> DeleteFilesAsync(
            RepairConfiguration repairConfiguration,
            CancellationToken cancellationToken)
        {
           string actionMessage =
                $"Attempting to delete files in folder {((DiskRepairPolicy)repairConfiguration.RepairPolicy).FolderPath} " +
                $"on node {repairConfiguration.NodeName}.";

            FabricHealerManager.RepairLogger.LogInfo(actionMessage);

            await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "RepairExecutor.DeleteFilesAsync",
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
                    await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.DeleteFilesAsync::HandledException",
                        $"Unable to delete {file}:{Environment.NewLine}{e}",
                        cancellationToken,
                        repairConfiguration).ConfigureAwait(false);
                }
            }

            if (maxFiles > 0 && initialCount > maxFiles && deletedFiles < maxFiles)
            {
                await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                       LogLevel.Info,
                       "RepairExecutor.DeleteFilesAsync::IncompleteOperation",
                       $"Unable to delete specified number of files ({maxFiles}).",
                       cancellationToken,
                       repairConfiguration).ConfigureAwait(false);

                return false;
            }
            
            if (maxFiles == 0 && deletedFiles < initialCount)
            {
                await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.DeleteFilesAsync::IncompleteOperation",
                        $"Unable to delete all files.",
                        cancellationToken,
                        repairConfiguration).ConfigureAwait(false); 

                return false;
            }

            await this.telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RepairExecutor.DeleteFilesAsync::Success",
                        $"Successfully deleted {(maxFiles > 0 ? "up to " + maxFiles.ToString() : "all")} files in {targetFolderPath}",
                        cancellationToken,
                        repairConfiguration).ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// Returns a machine name string, given a fabric node name.
        /// </summary>
        /// <param name="nodeName">Fabric node name</param>
        /// <param name="cancellationToken"></param>
        internal async Task<string>
            GetMachineHostNameFromFabricNodeNameAsync(string nodeName, CancellationToken cancellationToken)
        {
            try
            {
                var nodes = await this.fabricClient.QueryManager.GetNodeListAsync(
                                   nodeName,
                                   FabricHealerManager.ConfigSettings.AsyncTimeout,
                                   cancellationToken).ConfigureAwait(true);

                Node targetNode = nodes.Count > 0 ? nodes[0] : null;

                if (targetNode == null)
                {
                    return null;
                }

                string ipOrDnsName = targetNode?.IpAddressOrFQDN;
                var hostEntry = await Dns.GetHostEntryAsync(ipOrDnsName).ConfigureAwait(false);
                var machineName = hostEntry.HostName;

                return machineName;
            }
            catch (Exception e) when
                (e is ArgumentException
                || e is SocketException
                || e is OperationCanceledException
                || e is TimeoutException)
            {
                FabricHealerManager.RepairLogger.LogWarning(
                    $"Unable to determine machine host name from Fabric node name {nodeName}:{Environment.NewLine}{e}");
            }

            return null;
        }
    }
}