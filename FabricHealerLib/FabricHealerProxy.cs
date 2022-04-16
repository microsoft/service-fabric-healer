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
using FabricHealerLib.Exceptions;

namespace FabricHealerLib
{
    /// <summary>
    /// FabricHealer utility library that provides a very simple and reliable way to share Service Fabric entity repair information to FabricHealer service running in the same cluster.
    /// </summary>
    public static class FabricHealerProxy
    {
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
        public static async Task RepairEntityAsync(RepairData repairData, CancellationToken cancellationToken, TimeSpan repairDataLifetime = default)
        {
            var settings = new FabricClientSettings
            {
                HealthOperationTimeout = TimeSpan.FromSeconds(60),
                HealthReportSendInterval = TimeSpan.FromSeconds(1),
                HealthReportRetrySendInterval = TimeSpan.FromSeconds(3),
            };

            using (FabricClient fabricClient = new FabricClient(settings))
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

                // Polly retry policy and async execution. Any other type of exception shall bubble up to caller as they are no-ops.
                await Policy.Handle<FabricException>()
                                .Or<TimeoutException>()
                                .Or<HealthReportNotFoundException>()
                                .WaitAndRetryAsync(
                                    new[]
                                    {
                                        TimeSpan.FromSeconds(3),
                                        TimeSpan.FromSeconds(2),
                                        TimeSpan.FromSeconds(1)
                                    }).ExecuteAsync(
                                        () => RepairEntityAsyncInternal(
                                                repairData,
                                                repairDataLifetime,
                                                fabricClient,
                                                cancellationToken));
            }
        }

        private static async Task RepairEntityAsyncInternal(RepairData repairData, TimeSpan repairDataLifetime, FabricClient fabricClient, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
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

                if (!await GenerateHealthReportAsync(repairData, fabricClient, healthInformation, cancellationToken).ConfigureAwait(false))
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
        }

        private static async Task<bool> GenerateHealthReportAsync(RepairData repairData, FabricClient fabricClient, HealthInformation healthInformation, CancellationToken cancellationToken)
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

            return await HealthReportExistsAsync(repairData, fabricClient, cancellationToken);
        }

        private static async Task<bool> HealthReportExistsAsync(RepairData repairData, FabricClient fabricClient, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // don't retry..
                return true;
            }

            if (!string.IsNullOrWhiteSpace(repairData.ServiceName))
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

        private static bool TryValidateAndFixFabricUriString(string uriString, out Uri fixedUri)
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
    }
}