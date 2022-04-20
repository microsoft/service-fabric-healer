// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Health;
using Newtonsoft.Json;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using System.Fabric.Query;
using Polly;
using FabricHealerProxy.Exceptions;
using System.Collections.Generic;

namespace FabricHealerProxy
{
    /// <summary>
    /// FabricHealer utility library that provides a very simple and reliable way to share Service Fabric entity repair information to FabricHealer service running in the same cluster.
    /// If configured with supporting repair rules, then FabricHealer will attempt to repair the target entity as specified in the related logic rules. There are a number of pre-existing rules,
    /// so modify them to suit your needs. Without corresponding rules, FabricHealer will not act on the specially-crafted health reports this library produces.
    /// </summary>
    public sealed class FabricHealer
    {
        private static FabricHealer instance;

        private static readonly FabricClientSettings settings = new FabricClientSettings
        {
            HealthOperationTimeout = TimeSpan.FromSeconds(60),
            HealthReportSendInterval = TimeSpan.FromSeconds(1),
            HealthReportRetrySendInterval = TimeSpan.FromSeconds(3),
        };

        // Use one FC for the lifetime of the consuming SF service process that loads FabricHealerProxy.dll.
        private static readonly FabricClient fabricClient = new FabricClient(settings);
        private static readonly object instanceLock = new object();
        private static readonly object writeLock = new object();
        private static readonly TimeSpan defaultHealthReportTtl = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan maxDataLifeTime = defaultHealthReportTtl;

        // Instance tuple that stores RepairData objects for a specified duration (defaultHealthReportTtl).
        private List<(DateTime DateAdded, RepairData RepairData)> repairDataHistory =
                 new List<(DateTime DateAdded, RepairData RepairData)>();

        private FabricHealer()
        {
            if (repairDataHistory == null)
            {
                repairDataHistory = new List<(DateTime DateAdded, RepairData RepairData)>();
            }
        }

        /// <summary>
        /// FabricHealer.Proxy singleton. This is thread-safe.
        /// </summary>
        public static FabricHealer Proxy
        {
            get
            {
                if (instance == null)
                {
                    lock (instanceLock)
                    {
                        if (instance == null)
                        {
                            instance = new FabricHealer();
                        }
                    }
                }

                return instance;
            }
        }

        /// <summary>
        /// This function generates a specially-crafted Service Fabric Health Report that the FabricHealer service will understand and act upon given the facts supplied
        /// in the RepairData instance.
        /// </summary>
        /// <param name="repairData">A RepairData instance. This is a well-known (ITelemetryData) data type that contains facts that FabricHealer will use 
        /// in the execution of its entity repair logic rules and related mitigation functions.</param>
        /// <param name="cancellationToken">CancellationToken used to ensure this function stops processing when the token is cancelled.</param>
        /// <param name="repairDataLifetime">The amount of time for the repair data to remain active (TTL of associated health report). Default is 15 mins.</param>
        /// <exception cref="ArgumentNullException">Thrown when RepairData instance is null.</exception>
        /// <exception cref="FabricException">Thrown when an internal Service Fabric operation fails.</exception>
        /// <exception cref="FabricNodeNotFoundException">Thrown when specified RepairData.NodeName does not exist in the cluster.</exception>
        /// <exception cref="FabricServiceNotFoundException">Thrown when specified service doesn't exist in the cluster.</exception>
        /// <exception cref="MissingRepairDataException">Thrown when RepairData instance is missing values for required non-null members (E.g., NodeName).</exception>
        /// <exception cref="UriFormatException">Thrown when required ApplicationName or ServiceName value is a malformed Uri string.</exception>
        /// <exception cref="TimeoutException">Thrown when internal Fabric client API calls timeout.</exception>
        public async Task RepairEntityAsync(RepairData repairData, CancellationToken cancellationToken, TimeSpan repairDataLifetime = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (repairData.HealthState == HealthState.Ok)
            {
                return;
            }

            if (repairData == null)
            {
                throw new ArgumentNullException("Supplied null for repairData argument. You must supply an instance of RepairData.");
            }

            if (string.IsNullOrEmpty(repairData.NodeName))
            {
                throw new MissingRepairDataException("RepairData.NodeName is a required field.");
            }

            // Polly retry policy and async execution. Any other type of exception shall bubble up to caller as they are no-ops.
            await Policy.Handle<HealthReportNotFoundException>()
                            .Or<FabricException>()
                            .Or<TimeoutException>()
                            .WaitAndRetryAsync(
                                new[]
                                {
                                    TimeSpan.FromSeconds(1),
                                    TimeSpan.FromSeconds(5),
                                    TimeSpan.FromSeconds(10),
                                    TimeSpan.FromSeconds(15)
                                }).ExecuteAsync(
                                    () => RepairEntityAsyncInternal(
                                            repairData,
                                            repairDataLifetime,
                                            fabricClient,
                                            cancellationToken)).ConfigureAwait(false);
        }

        /// <summary>
        /// This function generates a specially-crafted Service Fabric Health Report that the FabricHealer service will understand and act upon given the facts supplied
        /// in the RepairData instance. Use this function to supply a list or array of RepairData objects.
        /// </summary>
        /// <param name="repairDataCollection">A collection of RepairData instances. RepairData is a well-known (ITelemetryData) data ty[e that contains facts which FabricHealer will use 
        /// in the execution of its entity repair logic rules and related mitigation functions.</param>
        /// <param name="cancellationToken">CancellationToken used to ensure this function stops processing when the token is cancelled.</param>
        /// <param name="repairDataLifetime">The amount of time for the repair data to remain active (TTL of associated health report). Default is 15 mins.</param>
        /// <exception cref="ArgumentNullException">Thrown when RepairData instance is null.</exception>
        /// <exception cref="FabricException">Thrown when an internal Service Fabric operation fails.</exception>
        /// <exception cref="FabricNodeNotFoundException">Thrown when specified RepairData.NodeName does not exist in the cluster.</exception>
        /// <exception cref="FabricServiceNotFoundException">Thrown when specified service doesn't exist in the cluster.</exception>
        /// <exception cref="MissingRepairDataException">Thrown when RepairData instance is missing values for required non-null members (E.g., NodeName).</exception>
        /// <exception cref="UriFormatException">Thrown when required ApplicationName or ServiceName value is a malformed Uri string.</exception>
        /// <exception cref="TimeoutException">Thrown when internal Fabric client API calls timeout.</exception>
        public async Task RepairEntityAsync(IEnumerable<RepairData> repairDataCollection, CancellationToken cancellationToken, TimeSpan repairDataLifetime = default)
        {
            foreach (var repairData in repairDataCollection)
            {
                await RepairEntityAsync(repairData, cancellationToken, repairDataLifetime).ConfigureAwait(false);
            }
        }

        private async Task RepairEntityAsyncInternal(RepairData repairData, TimeSpan repairDataLifetime, FabricClient fabricClient, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // Remove expired repair data.
            lock (writeLock)
            {
                ManageRepairDataHistory();
            }

            if (string.IsNullOrWhiteSpace(repairData.ApplicationName))
            {
                // Application (0) EntityType enum default. Check to see if only Service was supplied. OR, check to see if only NodeName was supplied.
                if (!string.IsNullOrWhiteSpace(repairData.ServiceName) && repairData.EntityType == EntityType.Application)
                {
                    repairData.EntityType = EntityType.Service;
                }
                else if (!string.IsNullOrEmpty(repairData.NodeName) && repairData.EntityType == EntityType.Application)
                {
                    repairData.EntityType = EntityType.Node;

                    if (string.IsNullOrWhiteSpace(repairData.NodeType))
                    {
                        NodeList nodes = await fabricClient.QueryManager.GetNodeListAsync(repairData.NodeName, TimeSpan.FromSeconds(30), cancellationToken);

                        if (nodes == null || nodes.Count == 0)
                        {
                            throw new FabricNodeNotFoundException($"NodeName {repairData.NodeName} does not exist in this cluster.");
                        }

                        repairData.NodeType = nodes[0].NodeType;
                    }
                }
            }

            try
            {
                if (string.IsNullOrWhiteSpace(repairData.Source))
                {
                    CodePackageActivationContext context =
                        await FabricRuntime.GetActivationContextAsync(TimeSpan.FromSeconds(30), cancellationToken);

                    repairData.Source = context.GetServiceManifestName() + "_" + "FabricHealerProxy";
                }

                // Support for repair data that does not contain replica/partition facts for service level repair.
                switch (repairData.EntityType)
                {
                    case EntityType.Application when repairData.PartitionId == Guid.Empty || repairData.ReplicaId == 0:
                    case EntityType.DeployedApplication when repairData.PartitionId == Guid.Empty || repairData.ReplicaId == 0:
                    case EntityType.Service when repairData.PartitionId == Guid.Empty || repairData.ReplicaId == 0:
                    case EntityType.StatefulService when repairData.PartitionId == Guid.Empty || repairData.ReplicaId == 0:
                    case EntityType.StatelessService when repairData.PartitionId == Guid.Empty || repairData.ReplicaId == 0:

                        Uri appName, serviceName;

                        if (string.IsNullOrWhiteSpace(repairData.ApplicationName))
                        {
                            if (!TryValidateAndFixFabricUriString(repairData.ServiceName, out serviceName))
                            {
                                throw new UriFormatException($"Specified ServiceName, {repairData.ServiceName}, is invalid.");
                            }

                            ApplicationNameResult appNameResult =
                                await fabricClient.QueryManager.GetApplicationNameAsync(serviceName, TimeSpan.FromSeconds(60), cancellationToken);

                            appName = appNameResult.ApplicationName;
                            repairData.ApplicationName = appName.OriginalString;
                        }
                        else
                        {
                            if (!TryValidateAndFixFabricUriString(repairData.ApplicationName, out appName))
                            {
                                throw new UriFormatException($"Specified ApplicationName, {repairData.ApplicationName}, is invalid.");
                            }
                        }

                        if (repairData.PartitionId == null || repairData.ReplicaId == 0)
                        {
                            var depReplicas = await fabricClient.QueryManager.GetDeployedReplicaListAsync(repairData.NodeName, appName);
                            var depReplica =
                                depReplicas.First(r => r.ServiceName.OriginalString.ToLower() == repairData.ServiceName.ToLower() && r.ReplicaStatus == ServiceReplicaStatus.Ready);
                            Guid partitionId = depReplica.Partitionid;
                            long replicaId;

                            if (depReplica is DeployedStatefulServiceReplica depStatefulReplica)
                            {
                                if (depStatefulReplica.ReplicaRole == ReplicaRole.Primary || depStatefulReplica.ReplicaRole == ReplicaRole.ActiveSecondary)
                                {
                                    replicaId = depStatefulReplica.ReplicaId;
                                }
                                else
                                {
                                    return;
                                }
                            }
                            else
                            {
                                replicaId = (depReplica as DeployedStatelessServiceInstance).InstanceId;
                            }

                            repairData.PartitionId = partitionId;
                            repairData.ProcessId = depReplica.HostProcessId;
                            repairData.ReplicaId = replicaId;
                        }

                        if (string.IsNullOrWhiteSpace(repairData.Description))
                        {
                            repairData.Description = $"{repairData.Source} has put {repairData.ServiceName} into {repairData.HealthState}.";
                        }

                        if (string.IsNullOrWhiteSpace(repairData.Property))
                        {
                            repairData.Property = $"{repairData.NodeName}_{repairData.ServiceName.Remove(0, repairData.ApplicationName.Length + 1)}_{repairData.Metric ?? "FHRepair"}";
                        }
                        break;

                    case EntityType.Node:

                        if (string.IsNullOrWhiteSpace(repairData.Description))
                        {
                            repairData.Description = $"{repairData.Source} has put {repairData.NodeName} of type {repairData.NodeType} into {repairData.HealthState}.";
                        }

                        if (string.IsNullOrWhiteSpace(repairData.Property))
                        {
                            repairData.Property = $"{repairData.NodeName}_{repairData.Metric ?? repairData.NodeType}_FHRepair";
                        }
                        break;
                }

                if (repairDataLifetime == default)
                {
                    repairDataLifetime = defaultHealthReportTtl;
                }

                var healthInformation = new HealthInformation(repairData.Source, repairData.Property, repairData.HealthState)
                {
                    Description = JsonConvert.SerializeObject(repairData),
                    TimeToLive = repairDataLifetime,
                    RemoveWhenExpired = true
                };

                if (!await GenerateHealthReportAsync(repairData, healthInformation, cancellationToken).ConfigureAwait(false))
                {
                    // This will initiate a retry (see Polly.RetryPolicy definition in caller).
                    throw new HealthReportNotFoundException($"Health Report with sourceid {repairData.Source} and property {repairData.Property}) not found in HM database.");
                }
            }
            catch (Exception e) when (e is FabricServiceNotFoundException)
            {
                throw new FabricServiceNotFoundException($"Specified ServiceName {repairData.ServiceName} does not exist in the cluster.", e);
            }
            catch (Exception e) when (e is OperationCanceledException || e is TaskCanceledException)
            {
                return;
            }

            // Add repairData to history.
            lock (writeLock)
            {
                repairDataHistory.Add((DateTime.UtcNow, repairData));
            }
        }

        private void ManageRepairDataHistory()
        {
            for (int i = 0; i < repairDataHistory.Count; i++)
            {
                try
                {
                    var data = repairDataHistory[i];

                    if (DateTime.UtcNow.Subtract(data.DateAdded) > maxDataLifeTime)
                    {
                        _ = repairDataHistory.Remove(data);
                        --i;
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is IndexOutOfRangeException)
                {

                }
            }
        }

        private async Task<bool> GenerateHealthReportAsync(RepairData repairData, HealthInformation healthInformation, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // don't retry..
                return true;
            }

            var sendOptions = new HealthReportSendOptions { Immediate = true };

            switch (repairData.EntityType)
            {
                case EntityType.Application when repairData.ApplicationName != null:

                    var appHealthReport = new ApplicationHealthReport(new Uri(repairData.ApplicationName), healthInformation);
                    fabricClient.HealthManager.ReportHealth(appHealthReport, sendOptions);
                    break;

                case EntityType.Service when repairData.ServiceName != null:

                    var serviceHealthReport = new ServiceHealthReport(new Uri(repairData.ServiceName), healthInformation);
                    fabricClient.HealthManager.ReportHealth(serviceHealthReport, sendOptions);
                    break;

                case EntityType.StatefulService when repairData.PartitionId != Guid.Empty && repairData.ReplicaId > 0:

                    var statefulServiceHealthReport = new StatefulServiceReplicaHealthReport(repairData.PartitionId, repairData.ReplicaId, healthInformation);
                    fabricClient.HealthManager.ReportHealth(statefulServiceHealthReport, sendOptions);
                    break;

                case EntityType.StatelessService when repairData.PartitionId != Guid.Empty && repairData.ReplicaId > 0:

                    var statelessServiceHealthReport = new StatelessServiceInstanceHealthReport(repairData.PartitionId, repairData.ReplicaId, healthInformation);
                    fabricClient.HealthManager.ReportHealth(statelessServiceHealthReport, sendOptions);
                    break;

                case EntityType.Partition when repairData.PartitionId != Guid.Empty:
                    var partitionHealthReport = new PartitionHealthReport(repairData.PartitionId, healthInformation);
                    fabricClient.HealthManager.ReportHealth(partitionHealthReport, sendOptions);
                    break;

                case EntityType.DeployedApplication when repairData.ApplicationName != null:

                    var deployedApplicationHealthReport = new DeployedApplicationHealthReport(new Uri(repairData.ApplicationName), repairData.NodeName, healthInformation);
                    fabricClient.HealthManager.ReportHealth(deployedApplicationHealthReport, sendOptions);
                    break;

                case EntityType.Node:
                    var nodeHealthReport = new NodeHealthReport(repairData.NodeName, healthInformation);
                    fabricClient.HealthManager.ReportHealth(nodeHealthReport, sendOptions);
                    break;
            }

            return await HealthReportExistsAsync(repairData, fabricClient, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> HealthReportExistsAsync(RepairData repairData, FabricClient fabricClient, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // don't retry..
                return true;
            }

            if (!string.IsNullOrWhiteSpace(repairData.ApplicationName) && repairData.ApplicationName.ToLower() == "fabric:/system")
            {
                try
                {
                    var appHealth = await fabricClient.HealthManager.GetApplicationHealthAsync(new Uri("fabric:/System")).ConfigureAwait(false);
                    var unhealthyEvents =
                        appHealth.HealthEvents?.Where(
                            s => s.HealthInformation.SourceId == repairData.Source && s.HealthInformation.Property == repairData.Property);

                    if (unhealthyEvents?.Count() == 0)
                    {
                        return false;
                    }
                }
                catch (FabricException)
                {
                    return false;
                }
            }
            else if (!string.IsNullOrWhiteSpace(repairData.ServiceName))
            {
                try
                {
                    var serviceHealth = await fabricClient.HealthManager.GetServiceHealthAsync(new Uri(repairData.ServiceName)).ConfigureAwait(false);
                    var unhealthyEvents =
                        serviceHealth.HealthEvents?.Where(
                            s => s.HealthInformation.SourceId == repairData.Source && s.HealthInformation.Property == repairData.Property);

                    if (unhealthyEvents?.Count() == 0)
                    {
                        return false;
                    }
                }
                catch (FabricException)
                {
                    return false;
                }
            }
            else if (!string.IsNullOrWhiteSpace(repairData.NodeName))
            {
                // Node reports
                try
                {
                    var nodeHealth = await fabricClient.HealthManager.GetNodeHealthAsync(repairData.NodeName).ConfigureAwait(false);

                    var unhealthyNodeEvents =
                        nodeHealth.HealthEvents?.Where(
                            s => s.HealthInformation.SourceId == repairData.Source && s.HealthInformation.Property == repairData.Property);

                    if (unhealthyNodeEvents?.Count() == 0)
                    {
                        return false;
                    }
                }
                catch (FabricException)
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryValidateAndFixFabricUriString(string uriString, out Uri fixedUri)
        {
            try
            {
                /* Try and fix malformed app/service names, if possible. */

                if (!uriString.StartsWith("fabric:/"))
                {
                    uriString = uriString.Insert(0, "fabric:/");
                }

                if (uriString.Contains("://"))
                {
                    uriString = uriString.Replace("://", ":/");
                }

                if (uriString.Contains(" "))
                {
                    uriString = uriString.Replace(" ", string.Empty);
                }

                if (Uri.IsWellFormedUriString(uriString, UriKind.RelativeOrAbsolute))
                {
                    fixedUri = new Uri(uriString);
                    return true;
                }
            }
            catch (ArgumentException)
            {

            }

            fixedUri = null;
            return false;
        }

        private async Task ClearHealthReports()
        {
            await Policy.Handle<FabricException>()
                            .Or<TimeoutException>()
                            .WaitAndRetryAsync(
                                new[]
                                {
                                    TimeSpan.FromSeconds(1),
                                    TimeSpan.FromSeconds(3),
                                    TimeSpan.FromSeconds(5)
                                }).ExecuteAsync(() => ClearHealthReportsInternalAsync()).ConfigureAwait(false);
        }

        private async Task ClearHealthReportsInternalAsync()
        {
            for (int i = 0; i < repairDataHistory.Count; i++)
            {
                var repairData = repairDataHistory[i].RepairData;

                try
                {
                    var healthInformation = new HealthInformation(repairData.Source, repairData.Property, HealthState.Ok)
                    {
                        Description = "Clearing existing health reports from FabricHealerProxy",
                        TimeToLive = TimeSpan.FromMinutes(5),
                        RemoveWhenExpired = true
                    };

                    var sendOptions = new HealthReportSendOptions { Immediate = true };

                    switch (repairData.EntityType)
                    {
                        case EntityType.Application when repairData.ApplicationName != null:

                            var appHealthReport = new ApplicationHealthReport(new Uri(repairData.ApplicationName), healthInformation);
                            fabricClient.HealthManager.ReportHealth(appHealthReport, sendOptions);
                            break;

                        case EntityType.Service when repairData.ServiceName != null:

                            var serviceHealthReport = new ServiceHealthReport(new Uri(repairData.ServiceName), healthInformation);
                            fabricClient.HealthManager.ReportHealth(serviceHealthReport, sendOptions);
                            break;

                        case EntityType.StatefulService when repairData.PartitionId != Guid.Empty && repairData.ReplicaId > 0:

                            var statefulServiceHealthReport = new StatefulServiceReplicaHealthReport(repairData.PartitionId, repairData.ReplicaId, healthInformation);
                            fabricClient.HealthManager.ReportHealth(statefulServiceHealthReport, sendOptions);
                            break;

                        case EntityType.StatelessService when repairData.PartitionId != Guid.Empty && repairData.ReplicaId > 0:

                            var statelessServiceHealthReport = new StatelessServiceInstanceHealthReport(repairData.PartitionId, repairData.ReplicaId, healthInformation);
                            fabricClient.HealthManager.ReportHealth(statelessServiceHealthReport, sendOptions);
                            break;

                        case EntityType.Partition when repairData.PartitionId != Guid.Empty:
                            var partitionHealthReport = new PartitionHealthReport(repairData.PartitionId, healthInformation);
                            fabricClient.HealthManager.ReportHealth(partitionHealthReport, sendOptions);
                            break;

                        case EntityType.DeployedApplication when repairData.ApplicationName != null:

                            var deployedApplicationHealthReport = new DeployedApplicationHealthReport(new Uri(repairData.ApplicationName), repairData.NodeName, healthInformation);
                            fabricClient.HealthManager.ReportHealth(deployedApplicationHealthReport, sendOptions);
                            break;

                        case EntityType.Node:
                            var nodeHealthReport = new NodeHealthReport(repairData.NodeName, healthInformation);
                            fabricClient.HealthManager.ReportHealth(nodeHealthReport, sendOptions);
                            break;
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is FabricException || e is IndexOutOfRangeException|| e is TimeoutException)
                {

                }

                await Task.Delay(250).ConfigureAwait(false);

                lock (writeLock)
                {
                    repairDataHistory.RemoveAt(i);
                    --i;
                }
            }
        }

        /// <summary>
        /// Releases resources used by FabricHealer.Proxy, including cleaning up any active health reports.
        /// </summary>
        public async Task Close()
        {
            await ClearHealthReports();

            if (repairDataHistory != null)
            {
                lock (writeLock)
                {
                    repairDataHistory?.Clear();
                    repairDataHistory = null;

                    if (instance != null)
                    {
                        instance = null;
                    }
                }
            }
        }
    }
}