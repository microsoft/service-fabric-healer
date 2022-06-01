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

            // Do not write ETW/send Telemetry/create health report if the data is informational-only and verbose logging is not enabled.
            // This means only Warning and Error messages will be transmitted. In general, however, it is best to enable Verbose Logging (default)
            // in FabricHealer as it will not generate noisy local logs and you will have a complete record of mitigation history in your AI or LA workspace.
            if (!verboseLogging && level == LogLevel.Info)
            {
                return;
            }

            // Service Fabric health report generation.
            var healthReporter = new FabricHealthReporter(fabricClient, logger);
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
                EmitLogEvent = true
            };

            healthReporter.ReportHealthToServiceFabric(healthReport);

            if (!FabricHealerManager.ConfigSettings.EtwEnabled && !FabricHealerManager.ConfigSettings.TelemetryEnabled)
            {
                return;
            }

            var telemData = new TelemetryData()
            {
                ApplicationName = repairData?.ApplicationName,
                ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                Code = repairData?.Code,
                ContainerId = repairData?.ContainerId,
                Description = description,
                EntityType = reportType,
                HealthState = healthState,
                Metric = repairAction,
                NodeName = repairData?.NodeName,
                NodeType = repairData?.NodeType,
                ObserverName = repairData?.ObserverName,
                PartitionId = repairData?.PartitionId,
                ProcessId = repairData != null ? repairData.ProcessId : -1,
                Property = property,
                ReplicaId = repairData != null ? repairData.ReplicaId : 0,
                RepairPolicy = repairData?.RepairPolicy ?? new RepairPolicy(),
                ServiceName = repairData?.ServiceName,
                Source = source,
                SystemServiceProcessName = repairData?.SystemServiceProcessName,
                Value = repairData != null ? repairData.Value : -1,
            };

            // Telemetry.
            if (FabricHealerManager.ConfigSettings.TelemetryEnabled)
            {
                await telemetryClient?.ReportMetricAsync(telemData, token);
            }

            // ETW.
            if (FabricHealerManager.ConfigSettings.EtwEnabled)
            {
                if (healthState == HealthState.Ok || healthState == HealthState.Unknown || healthState == HealthState.Invalid)
                {
                    ServiceEventSource.Current.DataTypeWriteInfo(RepairConstants.EventSourceEventName, telemData);
                }
                else if (healthState == HealthState.Warning)
                {
                    ServiceEventSource.Current.DataTypeWriteWarning(RepairConstants.EventSourceEventName, telemData);
                }
                else
                {
                    ServiceEventSource.Current.DataTypeWriteError(RepairConstants.EventSourceEventName, telemData);
                }
            }
        }
    }
}
