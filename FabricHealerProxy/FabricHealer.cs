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
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace FabricHealer
{
    /// <summary>
    /// FabricHealer utility class that provides a very simple and reliable way for any .NET Service Fabric user service to share Service Fabric entity repair facts to the FabricHealer 
    /// service running in the same cluster. If configured with supporting repair rules, then FabricHealer will attempt to repair the target entity using the supplied facts 
    /// in the entity-specific logic rules. There are a number of pre-existing logic rules covering the array of supported entities, so modify them to suit your specific needs. 
    /// FabricHealerProxy's RepairData is a crititcal type that holds user-supplied (and in some cases FabricHealerProxy-determined) values (facts) 
    /// that dictate which rules will be loaded by FabricHealer and passed to Guan for logic rule query execution that may or may not lead 
    /// to some repair (depends on the facts and the results of the logic programs that employ them).
    /// </summary>
    public sealed class Proxy
    {
        private const string FHProxyId = "FabricHealerProxy";
        private static Proxy instance;

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

        // Instance tuple that stores RepairData objects for a specified duration (defaultHealthReportTtl).
        private ConcurrentDictionary<string, (DateTime DateAdded, RepairFacts RepairData)> repairDataHistory =
                 new ConcurrentDictionary<string, (DateTime DateAdded, RepairFacts RepairData)>();
        CancellationTokenRegistration tokenRegistration;
        CancellationTokenSource cts = null;

        private Proxy()
        {
            if (repairDataHistory == null)
            {
                repairDataHistory = new ConcurrentDictionary<string, (DateTime DateAdded, RepairFacts RepairFacts)>();
            }
        }

        /// <summary>
        /// FabricHealerProxy.Proxy singleton. This is thread-safe.
        /// </summary>
        public static Proxy Instance
        {
            get
            {
                if (instance == null)
                {
                    lock (instanceLock)
                    {
                        if (instance == null)
                        {
                            instance = new Proxy();
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
        /// <param name="repairFacts">A RepairFacts instance. This is a well-known (ITelemetryData) data type that contains facts that FabricHealer will use 
        /// in the execution of its entity repair logic rules and related mitigation functions.</param>
        /// <param name="cancellationToken">CancellationToken used to ensure this function stops processing when the token is cancelled. Any existing (active) health report will be cleared
        /// when this token is cancelled.</param>
        /// <param name="repairDataLifetime">The amount of time for the repair data to remain active (TTL of associated health report). Default is 15 mins.</param>
        /// <exception cref="ArgumentNullException">Thrown when RepairData instance is null.</exception>
        /// <exception cref="FabricException">Thrown when an internal Service Fabric operation fails.</exception>
        /// <exception cref="NodeNotFoundException">Thrown when specified RepairData.NodeName does not exist in the cluster.</exception>
        /// <exception cref="FabricServiceNotFoundException">Thrown when specified service doesn't exist in the cluster.</exception>
        /// <exception cref="MissingRepairFactsException">Thrown when RepairData instance is missing values for required non-null members (E.g., NodeName).</exception>
        /// <exception cref="UriFormatException">Thrown when required ApplicationName or ServiceName value is a malformed Uri string.</exception>
        /// <exception cref="TimeoutException">Thrown when internal Fabric client API calls timeout.</exception>
        public async Task RepairEntityAsync(RepairFacts repairFacts, CancellationToken cancellationToken, TimeSpan repairDataLifetime = default)
        {
            if (cts == null)
            {
                lock (writeLock)
                {
                    if (cts == null)
                    {
                        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        tokenRegistration = cts.Token.Register(() => { Close(); });
                    }
                }
            }

            if (repairFacts.HealthState == HealthState.Ok)
            {
                return;
            }

            if (repairFacts == null)
            {
                throw new ArgumentNullException("Supplied null for repairData argument. You must supply an instance of RepairData.");
            }

            if (string.IsNullOrEmpty(repairFacts.NodeName))
            {
                throw new MissingRepairFactsException("RepairData.NodeName is a required field.");
            }

            try
            {
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
                                    })
                                .ExecuteAsync(
                                    () => RepairEntityAsyncInternal(
                                            repairFacts,
                                            repairDataLifetime,
                                            fabricClient,
                                            cancellationToken)).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // This can happen when the internal ExecuteAsync impl calls LINQ's First() on a zero-sized collection, for example.
                // This should not crash the containing process, so just capture it here.
                // TODO: Add a file logger so folks can debug issues they run into with the library..
            }
        }

        /// <summary>
        /// This function generates a specially-crafted Service Fabric Health Report that the FabricHealer service will understand and act upon given the facts supplied
        /// in the RepairData instance. Use this function to supply a list or array of RepairData objects.
        /// </summary>
        /// <param name="repairFactsCollection">A collection of RepairFacts instances. RepairFacts is a well-known (ITelemetryData) data type that contains facts which FabricHealer will use 
        /// in the execution of its entity repair logic rules and related mitigation functions.</param>
        /// <param name="cancellationToken">CancellationToken used to ensure this function stops processing when the token is cancelled. Any existing (active) health report will be cleared
        /// when this token is cancelled.</param>
        /// <param name="repairDataLifetime">The amount of time for the repair data to remain active (TTL of associated health report). Default is 15 mins.</param>
        /// <exception cref="ArgumentNullException">Thrown when supplied RepairData instance is null.</exception>
        /// <exception cref="FabricException">Thrown when an internal Service Fabric operation fails.</exception>
        /// <exception cref="NodeNotFoundException">Thrown when a specified node does not exist in the Service Fabric cluster.</exception>
        /// <exception cref="ServiceNotFoundException">Thrown when a specified service doesn't exist in the Service Fabric cluster.</exception>
        /// <exception cref="MissingRepairFactsException">Thrown when RepairData instance is missing values for required non-null members (E.g., NodeName).</exception>
        /// <exception cref="UriFormatException">Thrown when required ApplicationName or ServiceName value is a malformed Uri string.</exception>
        /// <exception cref="TimeoutException">Thrown when internal Fabric client API calls timeout.</exception>
        public async Task RepairEntityAsync(IEnumerable<RepairFacts> repairFactsCollection, CancellationToken cancellationToken, TimeSpan repairDataLifetime = default)
        {
            if (cts == null)
            {
                lock (writeLock)
                {
                    if (cts == null)
                    {
                        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        tokenRegistration = cts.Token.Register(() => { Close(); });
                    }
                }
            }

            foreach (var repairData in repairFactsCollection)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await RepairEntityAsync(repairData, cancellationToken, repairDataLifetime).ConfigureAwait(false);
            }
        }

        private async Task RepairEntityAsyncInternal(RepairFacts repairData, TimeSpan repairDataLifetime, FabricClient fabricClient, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // Remove expired repair data.
            ManageRepairDataHistory(cancellationToken);

            // Application (0) EntityType enum default. Check to see if only Service was supplied. OR, check to see if only NodeName was supplied.
            if (!string.IsNullOrWhiteSpace(repairData.ServiceName) && repairData.EntityType == EntityType.Application)
            {
                repairData.EntityType = EntityType.Service;
            }
            else if (!string.IsNullOrEmpty(repairData.NodeName) &&
                     ((string.IsNullOrWhiteSpace(repairData.ApplicationName) && repairData.EntityType == EntityType.Application) || 
                       repairData.EntityType == EntityType.Machine))
            {
                repairData.EntityType = repairData.EntityType == EntityType.Machine ? EntityType.Machine : EntityType.Node;

                if (string.IsNullOrWhiteSpace(repairData.NodeType))
                {
                    NodeList nodes = await fabricClient.QueryManager.GetNodeListAsync(repairData.NodeName, TimeSpan.FromSeconds(30), cancellationToken);

                    if (nodes == null || nodes.Count == 0)
                    {
                        throw new NodeNotFoundException($"NodeName {repairData.NodeName} does not exist in this cluster.");
                    }

                    repairData.NodeType = nodes[0].NodeType;
                }
            }

            try
            {
                if (string.IsNullOrWhiteSpace(repairData.Source))
                {
                    CodePackageActivationContext context =
                        await FabricRuntime.GetActivationContextAsync(TimeSpan.FromSeconds(30), cancellationToken);

                    repairData.Source = $"{context.GetServiceManifestName()}_{FHProxyId}";
                }
                else if (!repairData.Source.ToLower().EndsWith(FHProxyId.ToLower()))
                {
                    repairData.Source += $"_{FHProxyId}";
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
                            if (!TryValidateFixFabricUriString(repairData.ServiceName, out serviceName))
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
                            if (!TryValidateFixFabricUriString(repairData.ApplicationName, out appName))
                            {
                                throw new UriFormatException($"Specified ApplicationName, {repairData.ApplicationName}, is invalid.");
                            }
                        }

                        if (repairData.ApplicationName != "fabric:/System" && (repairData.PartitionId == null || repairData.ReplicaId == 0))
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
                            repairData.Description = $"{repairData.Source} has put " +
                                $"{(!string.IsNullOrWhiteSpace(repairData.ServiceName) ? repairData.ServiceName : repairData.ApplicationName)} into {repairData.HealthState}.";
                        }

                        if (string.IsNullOrWhiteSpace(repairData.Property))
                        {
                            if (repairData.ServiceName != null)
                            {
                                repairData.Property = $"{repairData.NodeName}_{repairData.ServiceName.Remove(0, repairData.ApplicationName.Length + 1)}_{repairData.Metric ?? "FHRepair"}";
                            }
                            else
                            {
                                repairData.Property = $"{repairData.NodeName}_{repairData.ApplicationName.Replace("fabric:/", "")}_{repairData.Metric ?? "FHRepair"}";
                            }
                        }
                        break;

                    case EntityType.Machine:
                    case EntityType.Node:

                        if (string.IsNullOrWhiteSpace(repairData.Description))
                        {
                            repairData.Description = $"{repairData.Source} has put {repairData.NodeName} into {repairData.HealthState}.";
                        }

                        if (string.IsNullOrWhiteSpace(repairData.Property))
                        {
                            repairData.Property = $"{repairData.NodeName}_{repairData.Metric ?? repairData.NodeType}_FHRepair_{(repairData.EntityType == EntityType.Node ? "FabricNode" : "Machine")}";
                        }
                        break;

                    case EntityType.Disk:

                        if (string.IsNullOrWhiteSpace(repairData.Description))
                        {
                            repairData.Description = $"{repairData.Source} has put {repairData.NodeName} into {repairData.HealthState}.";
                        }

                        if (string.IsNullOrWhiteSpace(repairData.Property))
                        {
                            repairData.Property = $"{repairData.NodeName}_FHRepair_Disk";
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
                throw new ServiceNotFoundException($"Specified ServiceName {repairData.ServiceName} does not exist in the cluster.");
            }
            catch (Exception e) when (e is OperationCanceledException || e is TaskCanceledException)
            {
                return;
            }

            // Add repairData to history.
            _ = repairDataHistory.TryAdd(repairData.Property, (DateTime.UtcNow, repairData));
        }

        private void ManageRepairDataHistory(CancellationToken cancellationToken)
        {
            for (int i = 0; i < repairDataHistory.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    var data = repairDataHistory.ElementAt(i);

                    if (DateTime.UtcNow.Subtract(data.Value.DateAdded) >= defaultHealthReportTtl)
                    {
                        _ = repairDataHistory.TryRemove(data.Key, out _);
                        --i;
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is IndexOutOfRangeException)
                {

                }
            }
        }

        private async Task<bool> GenerateHealthReportAsync(RepairFacts repairFacts, HealthInformation healthInformation, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return true;
            }

            var sendOptions = new HealthReportSendOptions { Immediate = true };

            switch (repairFacts.EntityType)
            {
                case EntityType.Application when repairFacts.ApplicationName != null:

                    var appHealthReport = new ApplicationHealthReport(new Uri(repairFacts.ApplicationName), healthInformation);
                    fabricClient.HealthManager.ReportHealth(appHealthReport, sendOptions);
                    break;

                case EntityType.Service when repairFacts.ServiceName != null:

                    var serviceHealthReport = new ServiceHealthReport(new Uri(repairFacts.ServiceName), healthInformation);
                    fabricClient.HealthManager.ReportHealth(serviceHealthReport, sendOptions);
                    break;

                case EntityType.StatefulService when repairFacts.PartitionId != Guid.Empty && repairFacts.ReplicaId > 0:

                    var statefulServiceHealthReport = new StatefulServiceReplicaHealthReport((Guid)repairFacts.PartitionId, repairFacts.ReplicaId, healthInformation);
                    fabricClient.HealthManager.ReportHealth(statefulServiceHealthReport, sendOptions);
                    break;

                case EntityType.StatelessService when repairFacts.PartitionId != Guid.Empty && repairFacts.ReplicaId > 0:

                    var statelessServiceHealthReport = new StatelessServiceInstanceHealthReport((Guid)repairFacts.PartitionId, repairFacts.ReplicaId, healthInformation);
                    fabricClient.HealthManager.ReportHealth(statelessServiceHealthReport, sendOptions);
                    break;

                case EntityType.Partition when repairFacts.PartitionId != Guid.Empty:
                    var partitionHealthReport = new PartitionHealthReport((Guid)repairFacts.PartitionId, healthInformation);
                    fabricClient.HealthManager.ReportHealth(partitionHealthReport, sendOptions);
                    break;

                case EntityType.DeployedApplication when repairFacts.ApplicationName != null:

                    var deployedApplicationHealthReport = new DeployedApplicationHealthReport(new Uri(repairFacts.ApplicationName), repairFacts.NodeName, healthInformation);
                    fabricClient.HealthManager.ReportHealth(deployedApplicationHealthReport, sendOptions);
                    break;

                case EntityType.Disk:
                case EntityType.Machine:
                case EntityType.Node:
                    var nodeHealthReport = new NodeHealthReport(repairFacts.NodeName, healthInformation);
                    fabricClient.HealthManager.ReportHealth(nodeHealthReport, sendOptions);
                    break;
            }

            return await VerifyHealthReportExistsAsync(repairFacts, fabricClient, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> VerifyHealthReportExistsAsync(RepairFacts repairFacts, FabricClient fabricClient, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(repairFacts.ApplicationName) && repairFacts.ApplicationName.ToLower() == "fabric:/system")
            {
                try
                {
                    var appHealth = await fabricClient.HealthManager.GetApplicationHealthAsync(new Uri("fabric:/System")).ConfigureAwait(false);
                    var unhealthyEvents =
                        appHealth.HealthEvents?.Where(
                            s => s.HealthInformation.SourceId == repairFacts.Source && s.HealthInformation.Property == repairFacts.Property);

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
            else if (!string.IsNullOrWhiteSpace(repairFacts.ServiceName))
            {
                try
                {
                    var serviceHealth = await fabricClient.HealthManager.GetServiceHealthAsync(new Uri(repairFacts.ServiceName)).ConfigureAwait(false);
                    var unhealthyEvents =
                        serviceHealth.HealthEvents?.Where(
                            s => s.HealthInformation.SourceId == repairFacts.Source && s.HealthInformation.Property == repairFacts.Property);

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
            else if (!string.IsNullOrWhiteSpace(repairFacts.NodeName))
            {
                // Node reports
                try
                {
                    var nodeHealth = await fabricClient.HealthManager.GetNodeHealthAsync(repairFacts.NodeName).ConfigureAwait(false);

                    var unhealthyNodeEvents =
                        nodeHealth.HealthEvents?.Where(
                            s => s.HealthInformation.SourceId == repairFacts.Source && s.HealthInformation.Property == repairFacts.Property);

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

        private bool TryValidateFixFabricUriString(string uriString, out Uri fixedUri)
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

        private void ClearHealthReports()
        {
            Policy.Handle<FabricException>()
                      .Or<TimeoutException>()
                      .WaitAndRetry(
                        new[]
                        {
                            TimeSpan.FromSeconds(1),
                            TimeSpan.FromSeconds(3),
                            TimeSpan.FromSeconds(5),
                            TimeSpan.FromSeconds(10)
                        })
                      .Execute(() => ClearHealthReportsInternal());
        }

        private void ClearHealthReportsInternal()
        {
            if (repairDataHistory?.Count == 0)
            {
                return;
            }

            for (int i = 0; i < repairDataHistory.Count; i++)
            {
                try
                {
                    var repairFacts = repairDataHistory.ElementAt(i).Value.RepairData;
                    var healthInformation = new HealthInformation(repairFacts.Source, repairFacts.Property, HealthState.Ok)
                    {
                        Description = $"Clearing existing {repairFacts.EntityType} health reports created by FabricHealerProxy",
                        TimeToLive = TimeSpan.FromMinutes(5),
                        RemoveWhenExpired = true
                    };
                    var sendOptions = new HealthReportSendOptions { Immediate = true };

                    switch (repairFacts.EntityType)
                    {
                        case EntityType.Application when repairFacts.ApplicationName != null:

                            var appHealthReport = new ApplicationHealthReport(new Uri(repairFacts.ApplicationName), healthInformation);
                            fabricClient.HealthManager.ReportHealth(appHealthReport, sendOptions);
                            break;

                        case EntityType.Service when repairFacts.ServiceName != null:

                            var serviceHealthReport = new ServiceHealthReport(new Uri(repairFacts.ServiceName), healthInformation);
                            fabricClient.HealthManager.ReportHealth(serviceHealthReport, sendOptions);
                            break;

                        case EntityType.StatefulService when repairFacts.PartitionId != Guid.Empty && repairFacts.ReplicaId > 0:

                            var statefulServiceHealthReport = new StatefulServiceReplicaHealthReport((Guid)repairFacts.PartitionId, repairFacts.ReplicaId, healthInformation);
                            fabricClient.HealthManager.ReportHealth(statefulServiceHealthReport, sendOptions);
                            break;

                        case EntityType.StatelessService when repairFacts.PartitionId != Guid.Empty && repairFacts.ReplicaId > 0:

                            var statelessServiceHealthReport = new StatelessServiceInstanceHealthReport((Guid)repairFacts.PartitionId, repairFacts.ReplicaId, healthInformation);
                            fabricClient.HealthManager.ReportHealth(statelessServiceHealthReport, sendOptions);
                            break;

                        case EntityType.Partition when repairFacts.PartitionId != Guid.Empty:
                            var partitionHealthReport = new PartitionHealthReport((Guid)repairFacts.PartitionId, healthInformation);
                            fabricClient.HealthManager.ReportHealth(partitionHealthReport, sendOptions);
                            break;

                        case EntityType.DeployedApplication when repairFacts.ApplicationName != null:

                            var deployedApplicationHealthReport = new DeployedApplicationHealthReport(new Uri(repairFacts.ApplicationName), repairFacts.NodeName, healthInformation);
                            fabricClient.HealthManager.ReportHealth(deployedApplicationHealthReport, sendOptions);
                            break;

                        case EntityType.Disk:
                        case EntityType.Machine:
                        case EntityType.Node:
                            var nodeHealthReport = new NodeHealthReport(repairFacts.NodeName, healthInformation);
                            fabricClient.HealthManager.ReportHealth(nodeHealthReport, sendOptions);
                            break;
                    }

                    _ = repairDataHistory.TryRemove(repairDataHistory.ElementAt(i).Key, out _);
                    --i;
                }
                catch (Exception e) when (e is ArgumentException || e is IndexOutOfRangeException)
                {

                }
            }
        }

        /// <summary>
        /// Clears instance data created by FabricHealer.Proxy, including cleaning up any active Service Fabric health reports.
        /// Note: calling Close does not cancel repairs that are in flight or in the FabricHealer internal repair queue.
        /// </summary>
        private void Close()
        {
            if (repairDataHistory != null)
            {
                ClearHealthReports();
                repairDataHistory?.Clear();
                repairDataHistory = null;
            }

            if (cts != null)
            {
                cts.Dispose();
                cts = null;
            }

            if (tokenRegistration != null)
            {
                tokenRegistration.Dispose();
            }

            if (instance != null)
            {
                instance = null;
            }
        }
    }
}