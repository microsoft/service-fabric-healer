// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Interfaces;
using FabricHealer.Repair;
using FabricHealer.TelemetryLib;
using System;
using System.Fabric;
using System.Fabric.Health;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FabricHealer.Utilities.Telemetry
{
    public class TelemetryUtilities
    {
        private readonly FabricClient fabricClient;
        private readonly StatelessServiceContext serviceContext;
        private readonly ITelemetryProvider telemetryClient;
        private readonly Logger logger;

        public TelemetryUtilities(FabricClient fabricClient, StatelessServiceContext serviceContext)
        {
            this.fabricClient = fabricClient;
            this.serviceContext = serviceContext;
            logger = new Logger(RepairConstants.RepairData)
            {
                EnableVerboseLogging = true,
            };

            if (!FabricHealerManager.ConfigSettings.TelemetryEnabled)
            {
                return;
            }

            telemetryClient = FabricHealerManager.ConfigSettings.TelemetryProvider switch
            {
                TelemetryProviderType.AzureApplicationInsights => new AppInsightsTelemetry(FabricHealerManager.ConfigSettings.AppInsightsInstrumentationKey),
                TelemetryProviderType.AzureLogAnalytics => new LogAnalyticsTelemetry(
                                                                FabricHealerManager.ConfigSettings.LogAnalyticsWorkspaceId,
                                                                FabricHealerManager.ConfigSettings.LogAnalyticsSharedKey,
                                                                FabricHealerManager.ConfigSettings.LogAnalyticsLogType),
                _ => telemetryClient
            };
        }

        /// <summary>
        /// Emits Repair telemetry to AppInsights (or some other external service),
        /// ETW (EventSource), and Health Event to Service Fabric.
        /// </summary>
        /// <param name="level">Log Level.</param>
        /// <param name="source">Err/Warning source id.</param>
        /// <param name="description">Message.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="repairData">repairData instance.</param>
        /// <returns></returns>
        public async Task EmitTelemetryEtwHealthEventAsync(
                            LogLevel level,
                            string source,
                            string description,
                            CancellationToken token,
                            TelemetryData repairData = null,
                            bool verboseLogging = true,
                            TimeSpan ttl = default,
                            string property = "RepairStateInformation",
                            EntityType reportType = EntityType.Node)
        {
            bool hasRepairInfo = repairData != null;
            string repairAction = string.Empty;
            source = source != "FabricHealer" ? source?.Insert(0, "FabricHealer.") : "FabricHealer";

            if (hasRepairInfo)
            {
                repairAction = Enum.GetName(typeof(RepairActionType), repairData.RepairPolicy.RepairAction);
            }

            HealthState healthState = level switch
            {
                LogLevel.Error => HealthState.Error,
                LogLevel.Warning => HealthState.Warning,
                _ => HealthState.Ok
            };

            // Do not write ETW/send Telemetry if the data is informational-only and verbose logging is not enabled.
            // This means only Warning and Error messages will be transmitted. In general, however, it is best to enable Verbose Logging (default)
            // in FabricHealer as it will not generate noisy local logs and you will have a complete record of mitigation steps in your AI or LA workspace.
            if (!verboseLogging && level == LogLevel.Info)
            {
                return;
            }

            // Local Logging
            logger.LogInfo(description);

            // Service Fabric health report generation.
            var healthReporter = new FabricHealthReporter(fabricClient);
            var healthReport = new HealthReport
            {
                AppName = reportType == EntityType.Application ? new Uri("fabric:/FabricHealer") : null,
                Code = repairData?.RepairPolicy?.RepairId,
                HealthMessage = description,
                NodeName = serviceContext.NodeContext.NodeName,
                EntityType = reportType,
                State = healthState,
                HealthReportTimeToLive = ttl == default ? TimeSpan.FromMinutes(5) : ttl,
                Property = property,
                SourceId = source,
            };

            healthReporter.ReportHealthToServiceFabric(healthReport);

            // Telemetry.
            if (FabricHealerManager.ConfigSettings.TelemetryEnabled)
            {
                var telemData = new TelemetryData()
                {
                    ApplicationName = repairData?.ApplicationName ?? string.Empty,
                    ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                    Description = description,
                    HealthState = healthState,
                    Metric = repairAction,
                    NodeName = repairData?.NodeName ?? string.Empty,
                    PartitionId = repairData?.PartitionId != null ? repairData.PartitionId : default,
                    ReplicaId = repairData != null ? repairData.ReplicaId : 0,
                    ServiceName = repairData?.ServiceName ?? string.Empty,
                    Source = source,
                    SystemServiceProcessName = repairData?.SystemServiceProcessName ?? string.Empty,
                };

                await telemetryClient?.ReportMetricAsync(telemData, token);
            }

            // ETW.
            if (FabricHealerManager.ConfigSettings.EtwEnabled)
            {
                ServiceEventSource.Current.Write(
                    RepairConstants.EventSourceEventName,
                    new
                    {
                        ApplicationName = repairData?.ApplicationName ?? string.Empty,
                        ClusterInformation.ClusterInfoTuple.ClusterId,
                        Description = description,
                        HealthState = Enum.GetName(typeof(HealthState), healthState),
                        Metric = repairAction,
                        PartitionId = repairData?.PartitionId.ToString() ?? string.Empty,
                        ReplicaId = repairData?.ReplicaId.ToString() ?? string.Empty,
                        Level = level,
                        NodeName = repairData?.NodeName ?? string.Empty,
                        OS = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux",
                        ServiceName = repairData?.ServiceName ?? string.Empty,
                        Source = source,
                        SystemServiceProcessName = repairData?.SystemServiceProcessName ?? string.Empty,
                    });
            }
        }
    }
}
