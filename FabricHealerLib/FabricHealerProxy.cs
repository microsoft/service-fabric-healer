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

namespace FabricHealerLib
{
    /// <summary>
    /// FabricHealer utility library that provides a very simple, structured way to share Service Fabric entity repair information to FabricHealer 
    /// via Service Fabric entity health reporting.
    /// </summary>
    public class FabricHealerProxy : IDisposable
    {
        private const int MaxRetries = 3;
        private int _retried;
        private bool _disposedValue;
        private readonly FabricClient _fabricClient;

        /// <summary>
        /// Creates an instance of FabricHealerProxy.
        /// </summary>
        public FabricHealerProxy()
        {
            if (_fabricClient == null)
            {
                var settings = new FabricClientSettings
                {
                    HealthOperationTimeout = TimeSpan.FromSeconds(30),
                    HealthReportSendInterval  = TimeSpan.FromSeconds(1),
                    HealthReportRetrySendInterval = TimeSpan.FromSeconds(3)
                };

                _fabricClient = new FabricClient(settings);
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
        /// <exception cref="MissingRequiredDataException">Thrown when RepairData instance is missing values for required non-null members (E.g., NodeName).</exception>
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
                throw new MissingRequiredDataException("RepairData.NodeName is a required field.");
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
                        NodeList nodes = await _fabricClient.QueryManager.GetNodeListAsync(repairData.NodeName, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

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
                        await FabricRuntime.GetActivationContextAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    
                    repairData.Source = context.GetServiceManifestName();
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
                                await _fabricClient.QueryManager.GetApplicationNameAsync(serviceName, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                            
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
                            var depReplicas = await _fabricClient.QueryManager.GetDeployedReplicaListAsync(repairData.NodeName, appName).ConfigureAwait(false);
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
                            repairData.Description = $"{repairData.ServiceName} has been designated as Unhealthy by {repairData.Source}.";
                        }

                        if (string.IsNullOrWhiteSpace(repairData.Property))
                        {
                            repairData.Property = $"{repairData.NodeName}_{repairData.ServiceName.Remove(0, repairData.ApplicationName.Length + 1)}_{repairData.Metric ?? "FHRepair"}";
                        }
                        break;

                    case EntityType.Node:

                        if (string.IsNullOrWhiteSpace(repairData.Description))
                        {
                            repairData.Description = $"{repairData.NodeName} of type {repairData.NodeType} has been designated as Unhealthy by {repairData.Source}.";
                        }

                        if (string.IsNullOrWhiteSpace(repairData.Property))
                        {
                            repairData.Property = $"{repairData.NodeName}_{repairData.Metric ?? repairData.NodeType}_FHRepair";
                        }
                        break;
                }

                TimeSpan timeToLive = repairDataLifetime;

                if (timeToLive == default)
                {
                    timeToLive = TimeSpan.FromMinutes(20);
                }

                var healthInformation = new HealthInformation(repairData.Source, repairData.Property, repairData.HealthState)
                {
                    Description = JsonConvert.SerializeObject(repairData),
                    TimeToLive = timeToLive,
                    RemoveWhenExpired = true
                };

                var sendOptions = new HealthReportSendOptions { Immediate = true };

                switch (repairData.EntityType)
                {
                    case EntityType.Application when repairData.ApplicationName != null:

                        var appHealthReport = new ApplicationHealthReport(new Uri(repairData.ApplicationName), healthInformation);
                        _fabricClient.HealthManager.ReportHealth(appHealthReport, sendOptions);
                        break;

                    case EntityType.Service when repairData.ServiceName != null:

                        var serviceHealthReport = new ServiceHealthReport(new Uri(repairData.ServiceName), healthInformation);
                        _fabricClient.HealthManager.ReportHealth(serviceHealthReport, sendOptions);
                        break;

                    case EntityType.StatefulService when repairData.PartitionId != Guid.Empty && repairData.ReplicaId > 0:

                        var statefulServiceHealthReport = new StatefulServiceReplicaHealthReport(repairData.PartitionId, repairData.ReplicaId, healthInformation);
                        _fabricClient.HealthManager.ReportHealth(statefulServiceHealthReport, sendOptions);
                        break;

                    case EntityType.StatelessService when repairData.PartitionId != Guid.Empty && repairData.ReplicaId > 0:

                        var statelessServiceHealthReport = new StatelessServiceInstanceHealthReport(repairData.PartitionId, repairData.ReplicaId, healthInformation);
                        _fabricClient.HealthManager.ReportHealth(statelessServiceHealthReport, sendOptions);
                        break;

                    case EntityType.Partition when repairData.PartitionId != Guid.Empty:
                        var partitionHealthReport = new PartitionHealthReport(repairData.PartitionId, healthInformation);
                        _fabricClient.HealthManager.ReportHealth(partitionHealthReport, sendOptions);
                        break;

                    case EntityType.DeployedApplication when repairData.ApplicationName != null:

                        var deployedApplicationHealthReport = new DeployedApplicationHealthReport(new Uri(repairData.ApplicationName), repairData.NodeName, healthInformation);
                        _fabricClient.HealthManager.ReportHealth(deployedApplicationHealthReport, sendOptions);
                        break;

                    case EntityType.Node:
                        var nodeHealthReport = new NodeHealthReport(repairData.NodeName, healthInformation);
                        _fabricClient.HealthManager.ReportHealth(nodeHealthReport, sendOptions);
                        break;
                }
            }
            catch (Exception e) when (e is FabricServiceNotFoundException)
            {
                throw new FabricServiceNotFoundException($"Specified ServiceName {repairData.ServiceName} does not exist in the cluster.", e);
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                _retried++;

                if (_retried <= MaxRetries)
                {
                    await RepairEntityAsync(repairData, cancellationToken).ConfigureAwait(false);
                }

                throw;
            }
            catch (Exception e) when (e is OperationCanceledException || e is TaskCanceledException)
            {
                _retried = 0;
                return;
            }

            _retried = 0;
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

        private void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _fabricClient?.Dispose();
                }

                _disposedValue = true;
            }
        }

        /// <summary>
        /// Dispose.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}