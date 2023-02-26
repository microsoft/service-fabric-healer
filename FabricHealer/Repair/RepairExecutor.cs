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
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;
using System.Fabric.Description;

namespace FabricHealer.Repair
{
    public sealed class RepairExecutor
    {
        private const double MaxWaitTimeMinutesForNodeOperation = 60.0;

        public static async Task<RestartDeployedCodePackageResult> RestartDeployedCodePackageAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            try
            {
                string actionMessage =
                    "Attempting to restart deployed code package for service " +
                    $"{repairData.ServiceName} " +
                    $"({repairData.ReplicaId}) on Node {repairData.NodeName}.";

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RestartDeployedCodePackageAsync::Starting",
                        actionMessage,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                if (!TryGetGuid(repairData.PartitionId, out Guid partitionId)) 
                {
                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RestartDeployedCodePackageAsync::MissingPartition",
                        "Missing partition information (repairData.PartitionId is not a Guid representation)",
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return null;
                }

                PartitionSelector partitionSelector = PartitionSelector.PartitionIdOf(new Uri(repairData.ServiceName), partitionId);
                long replicaId = repairData.ReplicaId;
                Replica replica = null;

                // Verify target replica still exists.
                var replicaList = await FabricHealerManager.FabricClientSingleton.QueryManager.GetReplicaListAsync(
                                            partitionId,
                                            replicaId,
                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                            cancellationToken);
                
                if (replicaList.Any(r => r.ReplicaStatus == ServiceReplicaStatus.Ready))
                {
                    replica = replicaList.First(r => r.ReplicaStatus == ServiceReplicaStatus.Ready);
                }
                else
                {
                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"RestartCodePackage::Failure({repairData.ReplicaId})",
                            $"Replica {repairData.ReplicaId} not found in partition {repairData.PartitionId}.",
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return null;
                }

                ReplicaSelector replicaSelector = ReplicaSelector.ReplicaIdOf(partitionSelector, replica.Id);

                // CompletionMode must be set to DoNotVerify for stateless and stateful volatile services.
                CompletionMode completionMode = CompletionMode.DoNotVerify;

                if (replica.ServiceKind == ServiceKind.Stateful)
                {
                    ServiceDescription serviceDesc =
                        await FabricHealerManager.FabricClientSingleton.ServiceManager.GetServiceDescriptionAsync(
                                new Uri(repairData.ServiceName), FabricHealerManager.ConfigSettings.AsyncTimeout, cancellationToken);

                    if (serviceDesc is StatefulServiceDescription statefulReplicaDesc)
                    {
                        if (statefulReplicaDesc.HasPersistedState)
                        {
                            completionMode = CompletionMode.Verify;
                        }
                    }
                }

                RestartDeployedCodePackageResult restartCodePackageResult = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
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

                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
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

                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
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
                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RestartCodePackageAsync::Exception",
                        $"Execution failure:{e.Message}",
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
        /// Restarts a stateful replica.
        /// </summary>
        /// <param name="repairData">repairData instance.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns></returns>
        public static async Task<bool> RestartReplicaAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            try
            {
                // Make sure the replica still exists. \\

                if (!TryGetGuid(repairData.PartitionId, out Guid partitionId))
                {
                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RestartReplicaAsync::MissingPartition",
                            $"{repairData.PartitionId} does not exist.",
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return false;
                }

                var replicaList = await FabricHealerManager.FabricClientSingleton.QueryManager.GetReplicaListAsync(
                                            partitionId,
                                            repairData.ReplicaId,
                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                            cancellationToken);

                if (!replicaList.Any())
                {
                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RepairExecutor.RestartReplicaAsync",
                            $"Execution failure: Stateful replica {repairData.ReplicaId} " +
                            $"not found in partition {partitionId}.",
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return false;
                }

                try
                {
                    await FabricHealerManager.FabricClientSingleton.ServiceManager.RestartReplicaAsync(
                            repairData.NodeName,
                            partitionId,
                            repairData.ReplicaId,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            cancellationToken);
                }
                catch (FabricException fe)
                {
                    // This would mean the stateful service replica is volatile (no persisted state), so we have to Remove it.
                    if (fe.ErrorCode == FabricErrorCode.InvalidReplicaOperation && fe.InnerException.Message == "0x80071C3A")
                    {
                        await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RestartReplicaAsync::Volatile",
                        $"Attempting to remove volatile stateful replica {repairData.ReplicaId} on partition {partitionId} " +
                        $"on node {repairData.NodeName}.",
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                        return await RemoveReplicaAsync(repairData, cancellationToken);                    
                    }
                }

                string statusSuccess =
                        $"Successfully restarted stateful replica {repairData.ReplicaId} " +
                        $"on partition {partitionId} on node {repairData.NodeName}.";

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RestartReplicaAsync::Success",
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
                    $"Exception Info: {e.Message}";

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        $"RestartReplica::Exception",
                        err,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                FabricHealerManager.RepairHistory.FailedRepairs++;
                return false;
            }

            return true;
        }

        public static async Task<bool> RestartSystemServiceProcessAsync(TelemetryData repairData, CancellationToken cancellationToken)
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

                    if (!OperatingSystem.IsWindows() && repairData.ProcessName.EndsWith(".dll"))
                    {
                        ps = GetLinuxDotnetProcessesByFirstArgument(repairData.ProcessName);
                    }
                    else
                    {
                        ps = Process.GetProcessesByName(repairData.ProcessName);
                    }

                    if (ps == null || ps.Length == 0)
                    {
                        string err =
                          $"Exception in RestartSystemServiceProcessAsync: Unable to restart process {repairData.ProcessName}. ps is null or empty";

                        await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Warning,
                                "RestartSystemServiceProcess::Failure",
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
                   $"Handled Exception in RestartSystemServiceProcessAsync: Unable to restart process {repairData.ProcessName} " +
                   $"on node {repairData.NodeName}.{Environment.NewLine}" +
                   $"Exception Info: {e.Message}";

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RestartSystemServiceProcess::HandledException",
                        err,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }
            catch (Exception e)
            {
                FabricHealerManager.RepairHistory.FailedRepairs++;

                string err =
                   $"Unhandled Exception in RestartSystemServiceProcessAsync: Unable to restart process {repairData.ProcessName} " +
                   $"on node {repairData.NodeName}.{Environment.NewLine}" +
                   $"Exception Info: {e.Message}";

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "RestartSystemServiceProcess::UnhandledException",
                        err,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }
            finally
            {
                p?.Dispose();
            }

            return true;
        }

        private static Process[] GetLinuxDotnetProcessesByFirstArgument(string argument)
        {
            if (OperatingSystem.IsWindows())
            {
                return null;
            }

            List<Process> result = new();
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
        public static async Task<bool> RemoveReplicaAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
            try
            {
                // Make sure the replica still exists. \\
                
                if (!TryGetGuid(repairData.PartitionId, out Guid partitionId))
                {
                    return false;
                }

                var replicaList = await FabricHealerManager.FabricClientSingleton.QueryManager.GetReplicaListAsync(
                                            partitionId,
                                            repairData.ReplicaId,
                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                            cancellationToken);

                if (!replicaList.Any(r => r.ReplicaStatus == ServiceReplicaStatus.Ready))
                {
                    FabricHealerManager.RepairHistory.FailedRepairs++;

                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "RemoveReplica::ReplicaNotFound",
                            $"Execution failure: Stateless instance {repairData.ReplicaId} " +
                            $"not found in partition {repairData.PartitionId}.",
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                    return false;
                }

                await FabricHealerManager.FabricClientSingleton.ServiceManager.RemoveReplicaAsync(
                        repairData.NodeName,
                        partitionId,
                        repairData.ReplicaId,
                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                        cancellationToken);

                string statusSuccess =
                    $"Successfully removed stateless instance {repairData.ReplicaId} " +
                    $"on partition {repairData.PartitionId} " +
                    $"on node {repairData.NodeName}.";

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "RemoveReplicaAsync::Success",
                        statusSuccess,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                UpdateRepairHistory(repairData);
                ClearEntityHealthWarnings(repairData);
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException || e is OperationCanceledException)
            {
                FabricHealerManager.RepairHistory.FailedRepairs++;

                string err =
                    $"Unable to remove stateless instance {repairData.ReplicaId} " +
                    $"on partition {repairData.PartitionId} " +
                    $"on node {repairData.NodeName}.{Environment.NewLine}" +
                    $"Exception Info: {e.Message}";

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "RemoveReplica::Exception",
                        err,
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }

            return true;
        }

        internal static async Task<bool> DeleteFilesAsync(TelemetryData repairData, CancellationToken cancellationToken)
        {
           string actionMessage =
                $"Attempting to delete files in folder {(repairData.RepairPolicy as DiskRepairPolicy).FolderPath} " +
                $"on node {repairData.NodeName}.";

            await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    "DeleteFiles::Start",
                    actionMessage,
                    cancellationToken,
                    repairData,
                    FabricHealerManager.ConfigSettings.EnableVerboseLogging);

            string targetFolderPath = (repairData.RepairPolicy as DiskRepairPolicy).FolderPath;

            if (!Directory.Exists(targetFolderPath))
            {
                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "DeleteFiles::DirectoryDoesNotExist",
                        $"The specified directory, {targetFolderPath}, does not exist.",
                        cancellationToken,
                        repairData,
                        FabricHealerManager.ConfigSettings.EnableVerboseLogging);

                return false;
            }

            DirectoryInfo dirInfo = new(targetFolderPath);
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
                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "DeleteFiles::NoFilesMatchSearchPattern",
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
                        await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                "DeleteFiles::HandledException",
                                $"Unable to delete {file}: {e.Message}",
                                cancellationToken,
                                repairData,
                                FabricHealerManager.ConfigSettings.EnableVerboseLogging);
                    }
                }

                if (maxFiles > 0 && initialCount > maxFiles && deletedFiles < maxFiles)
                {
                    FabricHealerManager.RepairHistory.FailedRepairs++;

                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "DeleteFiles::IncompleteOperation",
                            $"Unable to delete specified number of files ({maxFiles}).",
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);
                    
                    return false;
                }
            
                if (maxFiles == 0 && deletedFiles < initialCount)
                {
                    FabricHealerManager.RepairHistory.FailedRepairs++;

                    await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "DeleteFiles::IncompleteOperation",
                            "Unable to delete all files.",
                            cancellationToken,
                            repairData,
                            FabricHealerManager.ConfigSettings.EnableVerboseLogging);


                    return false;
                }

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "DeleteFiles::Success",
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
        /// Returns a machine name string, given a fabric node name.
        /// </summary>
        /// <param name="nodeName">Fabric node name</param>
        /// <param name="cancellationToken"></param>
        internal static async Task<string> GetMachineHostNameFromFabricNodeNameAsync(string nodeName, CancellationToken cancellationToken)
        {
            try
            {
                var nodes = await FabricHealerManager.FabricClientSingleton.QueryManager.GetNodeListAsync(
                                   nodeName,
                                   FabricHealerManager.ConfigSettings.AsyncTimeout,
                                   cancellationToken);

                Node targetNode = nodes.Count > 0 ? nodes[0] : null;

                if (targetNode == null)
                {
                    return null;
                }

                string ipOrDnsName = targetNode.IpAddressOrFQDN;
                var hostEntry = await Dns.GetHostEntryAsync(ipOrDnsName, cancellationToken);
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
        /// <param name="repairData">repairData instance.</param>
        private static void ClearEntityHealthWarnings(TelemetryData repairData)
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

                    case EntityType.StatefulService when TryGetGuid(repairData.PartitionId, out Guid partitionId) && repairData.ReplicaId > 0:

                        var statefulServiceHealthReport = new StatefulServiceReplicaHealthReport(partitionId, repairData.ReplicaId, healthInformation);
                        FabricHealerManager.FabricClientSingleton.HealthManager.ReportHealth(statefulServiceHealthReport, sendOptions);
                        break;

                    case EntityType.StatelessService when TryGetGuid(repairData.PartitionId, out Guid partitionId) && repairData.ReplicaId > 0:

                        var statelessServiceHealthReport = new StatelessServiceInstanceHealthReport(partitionId, repairData.ReplicaId, healthInformation);
                        FabricHealerManager.FabricClientSingleton.HealthManager.ReportHealth(statelessServiceHealthReport, sendOptions);
                        break;

                    case EntityType.Partition when TryGetGuid(repairData.PartitionId, out Guid partitionId):
                        var partitionHealthReport = new PartitionHealthReport(partitionId, healthInformation);
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

        /// <summary>
        /// This function ensures the input is in fact a Guid.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="input">An value representation (string or Guid) of a Guid structure.</param>
        /// <param name="guid">Guid that will be returned.</param>
        /// <returns>Boolean representing successful conversion and a Guid object instance (out).</returns>
        public static bool TryGetGuid<T>(T input, out Guid guid)
        {
            if (input == null)
            {
                guid = Guid.Empty;
                return false;
            }

            switch (input)
            {
                case Guid g:
                    guid = g;
                    return true;

                case string s:
                    return Guid.TryParse(s, out guid);

                default:
                    guid = Guid.Empty;
                    return false;
            }
        }
    }
}