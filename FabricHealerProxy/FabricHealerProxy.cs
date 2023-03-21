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
using System.Runtime.InteropServices;

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
    public sealed class FabricHealerProxy
    {
        private const string FHProxyId = "FabricHealerProxy";
        private static readonly FabricClientSettings _fcsettings = new()
        {
            HealthOperationTimeout = TimeSpan.FromSeconds(60),
            HealthReportSendInterval = TimeSpan.FromSeconds(1),
            HealthReportRetrySendInterval = TimeSpan.FromSeconds(3),
        };
        private static readonly object _instanceLock = new();
        private static readonly object _writeLock = new();
        private static readonly object _lock = new();
        private static readonly TimeSpan _defaultHealthReportTtl = TimeSpan.FromMinutes(15);
        private static FabricClient _fabricClient = null;
        private static FabricHealerProxy _instance = null;

        // Instance tuple that stores RepairData objects for a specified duration (defaultHealthReportTtl).
        private ConcurrentDictionary<string, (DateTime DateAdded, RepairFacts RepairData)> _repairDataHistory = new();
        private CancellationTokenRegistration _tokenRegistration;
        private CancellationTokenSource _cts = null;
        private readonly Logger _logger = null;

        private FabricHealerProxy()
        {
            _repairDataHistory ??= new ConcurrentDictionary<string, (DateTime DateAdded, RepairFacts RepairFacts)>();
            _logger = new Logger(FHProxyId);
        }

        // Use one FC for the lifetime of the consuming SF service process that loads FabricHealerProxy.dll.
        private static FabricClient FabricClientSingleton
        {
            get
            {
                if (_fabricClient == null)
                {
                    lock (_lock)
                    {
                        if (_fabricClient == null)
                        {
                            _fabricClient = new FabricClient(_fcsettings);
                            return _fabricClient;
                        }
                    }
                }
                else
                {
                    try
                    {
                        // This call with throw an ObjectDisposedException if fabricClient was disposed by, say, a plugin or if the runtime
                        // disposed of it for some random (unlikely..) reason. This is just a test to ensure it is not in a disposed state.
                        if (_fabricClient.Settings.HealthReportSendInterval > TimeSpan.MinValue)
                        {
                            return _fabricClient;
                        }
                    }
                    catch (Exception e) when (e is ObjectDisposedException or InvalidComObjectException)
                    {
                        lock (_lock)
                        {
                            _fabricClient = null;
                            _fabricClient = new FabricClient(_fcsettings);
                            return _fabricClient;
                        }
                    }
                }

                return _fabricClient;
            }
        }

        /// <summary>
        /// FabricHealerProxy static singleton. This is thread-safe.
        /// </summary>
        public static FabricHealerProxy Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new FabricHealerProxy();
                    }
                }

                return _instance;
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
            if (_cts == null)
            {
                lock (_writeLock)
                {
                    if (_cts == null)
                    {
                        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        _tokenRegistration = _cts.Token.Register(() => { Close(); });
                    }
                }
            }

            if (repairFacts.HealthState == HealthState.Ok)
            {
                return;
            }

            if (repairFacts == null)
            {
                string msg = "Supplied null for repairData argument. You must supply an instance of RepairData.";
                _logger.LogWarning(msg);
                throw new ArgumentNullException(msg);
            }

            if (string.IsNullOrEmpty(repairFacts.NodeName))
            {
                string msg = "RepairData.NodeName is a required field.";
                _logger.LogWarning(msg);
                throw new MissingRepairFactsException(msg);
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
                                            FabricClientSingleton,
                                            cancellationToken)).ConfigureAwait(false);
            }
            catch (InvalidOperationException ioe)
            {
                // This can happen when the internal ExecuteAsync impl calls LINQ's First() on a zero-sized collection, for example.
                // This should not crash the containing process, so just capture it here.
                _logger.LogWarning($"Unexpected exception in Policy.Handle{Environment.NewLine}{ioe}");
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
            if (_cts == null)
            {
                lock (_writeLock)
                {
                    if (_cts == null)
                    {
                        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        _tokenRegistration = _cts.Token.Register(() => { Close(); });
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

            // Support not specifying EntityType.
            if (!string.IsNullOrWhiteSpace(repairData.ServiceName) && repairData.EntityType == EntityType.Unknown)
            {
                repairData.EntityType = EntityType.Service;
            }
            else if (repairData.ReplicaId > 0 && repairData.EntityType == EntityType.Unknown)
            {
                repairData.EntityType = EntityType.Replica;
            }
            else if ((repairData.ProcessId > 0 || !string.IsNullOrWhiteSpace(repairData.ProcessName)) && repairData.EntityType == EntityType.Unknown)
            {
                repairData.EntityType = EntityType.Process;
            }
            else if (!string.IsNullOrEmpty(repairData.NodeName) &&
                     string.IsNullOrWhiteSpace(repairData.ApplicationName) && 
                     string.IsNullOrWhiteSpace(repairData.ServiceName) && 
                     repairData.EntityType == EntityType.Unknown || repairData.EntityType == EntityType.Machine)
            {
                repairData.EntityType = repairData.EntityType == EntityType.Machine ? EntityType.Machine : EntityType.Node;

                if (string.IsNullOrWhiteSpace(repairData.NodeType))
                {
                    NodeList nodes =
                        await fabricClient.QueryManager.GetNodeListAsync(repairData.NodeName, TimeSpan.FromSeconds(30), cancellationToken);

                    if (nodes == null || nodes.Count == 0)
                    {
                        string msg = $"NodeName {repairData.NodeName} does not exist in this cluster.";
                        _logger.LogWarning(msg);
                        throw new NodeNotFoundException(msg);
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
                    case EntityType.Application when !TryGetGuid(repairData.PartitionId, out _) || repairData.ReplicaId == 0:
                    case EntityType.DeployedApplication when !TryGetGuid(repairData.PartitionId, out _) || repairData.ReplicaId == 0:
                    case EntityType.Service when !TryGetGuid(repairData.PartitionId, out _) || repairData.ReplicaId == 0:
                    case EntityType.StatefulService when !TryGetGuid(repairData.PartitionId, out _) || repairData.ReplicaId == 0:
                    case EntityType.StatelessService when !TryGetGuid(repairData.PartitionId, out _) || repairData.ReplicaId == 0:

                        Uri appName, serviceName;

                        if (string.IsNullOrWhiteSpace(repairData.ApplicationName))
                        {
                            if (!TryValidateFixFabricUriString(repairData.ServiceName, out serviceName))
                            {
                                string msg = $"Specified ServiceName, {repairData.ServiceName}, is invalid.";
                                _logger.LogWarning(msg);
                                throw new UriFormatException(msg);
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
                                string msg = $"Specified ApplicationName, {repairData.ApplicationName}, is invalid.";
                                _logger.LogWarning(msg);
                                throw new UriFormatException(msg);
                            }
                        }

                        // Figure out PartitionId and Replica Id based on NodeName, ApplicationName and ServiceName facts.
                        if (repairData.ApplicationName != "fabric:/System" && (!TryGetGuid(repairData.PartitionId, out _) || repairData.ReplicaId == 0))
                        {
                            DeployedServiceReplicaList depReplicas = await fabricClient.QueryManager.GetDeployedReplicaListAsync(repairData.NodeName, appName);

                            if (!depReplicas.Any(r => r.ServiceName.OriginalString.ToLower() == repairData.ServiceName.ToLower() && r.ReplicaStatus == ServiceReplicaStatus.Ready))
                            {
                                throw new FabricServiceNotFoundException();
                            }

                            DeployedServiceReplica depReplica =
                                depReplicas.First(r => r.ServiceName.OriginalString.ToLower() == repairData.ServiceName.ToLower() && r.ReplicaStatus == ServiceReplicaStatus.Ready);
                            Guid partitionId = depReplica.Partitionid;
                            long replicaId;

                            if (depReplica is DeployedStatefulServiceReplica depStatefulReplica)
                            {
                                if (depStatefulReplica.ReplicaRole is ReplicaRole.Primary or ReplicaRole.ActiveSecondary)
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

                            repairData.PartitionId = partitionId.ToString();
                            repairData.ProcessId = depReplica.HostProcessId;
                            repairData.ReplicaId = replicaId;
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
                    repairDataLifetime = _defaultHealthReportTtl;
                }

                if (repairData.EntityType is EntityType.Application or EntityType.Service)
                {
                    if (string.IsNullOrWhiteSpace(repairData.Description))
                    {
                        repairData.Description = $"{repairData.Source} has put " +
                            $"{(!string.IsNullOrWhiteSpace(repairData.ServiceName) ? repairData.ServiceName : repairData.ApplicationName)} into {repairData.HealthState}.";
                    } 
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
                string msg = $"Specified ServiceName {repairData.ServiceName} does not exist in the cluster or has no replicas in Ready state.";
                _logger.LogWarning(msg);
                throw new ServiceNotFoundException(msg);
            }
            catch (Exception e) when (e is OperationCanceledException or TaskCanceledException)
            {
                return;
            }

            // Add repairData to history.
            _ = _repairDataHistory.TryAdd(repairData.Property, (DateTime.UtcNow, repairData));
        }

        private void ManageRepairDataHistory(CancellationToken cancellationToken)
        {
            for (int i = 0; i < _repairDataHistory.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    var data = _repairDataHistory.ElementAt(i);

                    if (DateTime.UtcNow.Subtract(data.Value.DateAdded) >= _defaultHealthReportTtl)
                    {
                        _ = _repairDataHistory.TryRemove(data.Key, out _);
                        --i;
                    }
                }
                catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException)
                {

                }
                catch (Exception e)
                {
                    _logger.LogError($"Unhandled exception in ManageRepairDataHistory:{Environment.NewLine}{e}");
                    throw;
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
                case EntityType.Application when !string.IsNullOrWhiteSpace(repairFacts.ApplicationName):
                case EntityType.Process when !string.IsNullOrWhiteSpace(repairFacts.ProcessName) || repairFacts.ProcessId > 0:

                    var appHealthReport = new ApplicationHealthReport(new Uri(repairFacts.ApplicationName), healthInformation);
                    FabricClientSingleton.HealthManager.ReportHealth(appHealthReport, sendOptions);
                    break;

                case EntityType.Service when !string.IsNullOrWhiteSpace(repairFacts.ServiceName):

                    var serviceHealthReport = new ServiceHealthReport(new Uri(repairFacts.ServiceName), healthInformation);
                    FabricClientSingleton.HealthManager.ReportHealth(serviceHealthReport, sendOptions);
                    break;

                case EntityType.StatefulService when TryGetGuid(repairFacts.PartitionId, out Guid partitionId) && repairFacts.ReplicaId > 0:

                    var statefulServiceHealthReport = new StatefulServiceReplicaHealthReport(partitionId, repairFacts.ReplicaId, healthInformation);
                    FabricClientSingleton.HealthManager.ReportHealth(statefulServiceHealthReport, sendOptions);
                    break;

                case EntityType.StatelessService when TryGetGuid(repairFacts.PartitionId, out Guid partitionId) && repairFacts.ReplicaId > 0:

                    var statelessServiceHealthReport = new StatelessServiceInstanceHealthReport(partitionId, repairFacts.ReplicaId, healthInformation);
                    FabricClientSingleton.HealthManager.ReportHealth(statelessServiceHealthReport, sendOptions);
                    break;

                case EntityType.Partition when TryGetGuid(repairFacts.PartitionId, out Guid partitionId):
                    var partitionHealthReport = new PartitionHealthReport(partitionId, healthInformation);
                    FabricClientSingleton.HealthManager.ReportHealth(partitionHealthReport, sendOptions);
                    break;

                case EntityType.DeployedApplication when !string.IsNullOrWhiteSpace(repairFacts.ApplicationName):

                    var deployedApplicationHealthReport = new DeployedApplicationHealthReport(new Uri(repairFacts.ApplicationName), repairFacts.NodeName, healthInformation);
                    FabricClientSingleton.HealthManager.ReportHealth(deployedApplicationHealthReport, sendOptions);
                    break;

                case EntityType.Disk:
                case EntityType.Machine:
                case EntityType.Node:
                    var nodeHealthReport = new NodeHealthReport(repairFacts.NodeName, healthInformation);
                    FabricClientSingleton.HealthManager.ReportHealth(nodeHealthReport, sendOptions);
                    break;
            }

            return await VerifyHealthReportExistsAsync(repairFacts, FabricClientSingleton, cancellationToken).ConfigureAwait(false);
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
                catch (Exception e)
                {
                    _logger.LogError($"Unhandled exception in VerifyHealthReportExistsAsync:{Environment.NewLine}{e}");
                    throw;
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
                catch (Exception e)
                {
                    _logger.LogError($"Unhandled exception in VerifyHealthReportExistsAsync:{Environment.NewLine}{e}");
                    throw;
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
                catch (Exception e)
                {
                    _logger.LogError($"Unhandled exception in VerifyHealthReportExistsAsync:{Environment.NewLine}{e}");
                    throw;
                }
            }

            return true;
        }

        private static bool TryValidateFixFabricUriString(string uriString, out Uri fixedUri)
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
                      .Or<HealthReportNotFoundException>()
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
            if (_repairDataHistory?.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _repairDataHistory.Count; i++)
            {
                try
                {
                    var repairFacts = _repairDataHistory.ElementAt(i).Value.RepairData;
                    
                    if (repairFacts == null)
                    {
                        continue;
                    }

                    var healthInformation = new HealthInformation(repairFacts.Source, repairFacts.Property, HealthState.Ok)
                    {
                        Description = $"Clearing existing {repairFacts.EntityType} health report created by FabricHealerProxy",
                        TimeToLive = TimeSpan.FromMinutes(5),
                        RemoveWhenExpired = true
                    };
                    var sendOptions = new HealthReportSendOptions { Immediate = true };

                    switch (repairFacts.EntityType)
                    {
                        case EntityType.Application when repairFacts.ApplicationName != null:
                        
                        // System Service repair (which is a process restart, ApplicationName = fabric:/System).
                        case EntityType.Process when repairFacts.ProcessName != null:
                            var appHealthReport = new ApplicationHealthReport(new Uri(repairFacts.ApplicationName), healthInformation);
                            FabricClientSingleton.HealthManager.ReportHealth(appHealthReport, sendOptions);
                            break;

                        case EntityType.Service when repairFacts.ServiceName != null:

                            var serviceHealthReport = new ServiceHealthReport(new Uri(repairFacts.ServiceName), healthInformation);
                            FabricClientSingleton.HealthManager.ReportHealth(serviceHealthReport, sendOptions);
                            break;

                        case EntityType.StatefulService when TryGetGuid(repairFacts.PartitionId, out Guid partitionId) && repairFacts.ReplicaId > 0:

                            var statefulServiceHealthReport = new StatefulServiceReplicaHealthReport(partitionId, repairFacts.ReplicaId, healthInformation);
                            FabricClientSingleton.HealthManager.ReportHealth(statefulServiceHealthReport, sendOptions);
                            break;

                        case EntityType.StatelessService when TryGetGuid(repairFacts.PartitionId, out Guid partitionId) && repairFacts.ReplicaId > 0:

                            var statelessServiceHealthReport = new StatelessServiceInstanceHealthReport(partitionId, repairFacts.ReplicaId, healthInformation);
                            FabricClientSingleton.HealthManager.ReportHealth(statelessServiceHealthReport, sendOptions);
                            break;

                        case EntityType.Partition when TryGetGuid(repairFacts.PartitionId, out Guid partitionId):
                            var partitionHealthReport = new PartitionHealthReport(partitionId, healthInformation);
                            FabricClientSingleton.HealthManager.ReportHealth(partitionHealthReport, sendOptions);
                            break;

                        case EntityType.DeployedApplication when repairFacts.ApplicationName != null:

                            var deployedApplicationHealthReport = new DeployedApplicationHealthReport(new Uri(repairFacts.ApplicationName), repairFacts.NodeName, healthInformation);
                            FabricClientSingleton.HealthManager.ReportHealth(deployedApplicationHealthReport, sendOptions);
                            break;

                        case EntityType.Disk:
                        case EntityType.Machine:
                        case EntityType.Node:
                            var nodeHealthReport = new NodeHealthReport(repairFacts.NodeName, healthInformation);
                            FabricClientSingleton.HealthManager.ReportHealth(nodeHealthReport, sendOptions);
                            break;
                    }

                    if (!VerifyHealthReportExistsAsync(repairFacts, FabricClientSingleton, _cts.Token).Result)
                    {
                        throw new HealthReportNotFoundException();
                    }

                    _ = _repairDataHistory.TryRemove(_repairDataHistory.ElementAt(i).Key, out _);
                    --i;
                }
                catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException)
                {

                }
                catch (Exception e)
                {
                    _logger.LogError($"Unhandled exception in ClearHealthReportsInternal:{Environment.NewLine}{e}");
                    throw;
                }
            }
        }

        private static bool TryGetGuid<T>(T input, out Guid guid)
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

        /// <summary>
        /// Closes the FabricHealerProxy.Instance, disposes all IDisposable objects currently in memory, and clears all health data and health reports.
        /// Note: calling Close does not cancel repairs that are in flight or in the FabricHealer internal repair queue. This function is called automatically when
        /// a consuming SF service closes. You do not need to call this directly unless that is your intention.
        /// </summary>
        public void Close()
        {
            if (_repairDataHistory != null)
            {
                ClearHealthReports();
                _repairDataHistory?.Clear();
                _repairDataHistory = null;
            }

            if (_cts != null)
            {
                _cts.Dispose();
                _cts = null;
            }

            _tokenRegistration.Dispose();

            if (_instance != null)
            {
                _instance = null;
            }
        }
    }
}