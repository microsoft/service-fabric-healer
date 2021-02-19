// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Interfaces;
using FabricHealer.Repair;
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

        public TelemetryUtilities(FabricClient fabricClient, StatelessServiceContext serviceContext)
        {
            this.fabricClient = fabricClient;
            this.serviceContext = serviceContext;

            if (FabricHealerManager.ConfigSettings != null && FabricHealerManager.ConfigSettings.TelemetryEnabled)
            {
                switch (FabricHealerManager.ConfigSettings.TelemetryProvider)
                {
                    case TelemetryProviderType.AzureApplicationInsights:
                    {
                        this.telemetryClient = new AppInsightsTelemetry(FabricHealerManager.ConfigSettings.AppInsightsInstrumentationKey);

                        break;
                    }
                    case TelemetryProviderType.AzureLogAnalytics:
                    {
                        this.telemetryClient = new LogAnalyticsTelemetry(
                            FabricHealerManager.ConfigSettings.LogAnalyticsWorkspaceId,
                            FabricHealerManager.ConfigSettings.LogAnalyticsSharedKey,
                            FabricHealerManager.ConfigSettings.LogAnalyticsLogType);

                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Emits Repair telemetry to AppInsights (or some other external service),
        /// ETW (EventSource), and Health Event to Service Fabric.
        /// </summary>
        /// <param name="level">Log Level.</param>
        /// <param name="source">Err/Warning source id.</param>
        /// <param name="description">Message.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="node">Node name.</param>
        /// <param name="repairAction">Repair action.</param>
        /// <returns></returns>
        public async Task EmitTelemetryEtwHealthEventAsync(
            LogLevel level,
            string source,
            string description,
            CancellationToken token,
            RepairConfiguration repairConfig = null)
        {
            bool hasRepairInfo = repairConfig != null;
            string repairAction = string.Empty;
            
            if (source != null)
            {
                source = source.Insert(0, "FabricHealer.");
            }

            if (hasRepairInfo)
            {
                repairAction = Enum.GetName(typeof(RepairActionType), repairConfig.RepairPolicy.RepairAction);
            }

            HealthState healthState = HealthState.Ok;

            if (level == LogLevel.Error)
            {
                healthState = HealthState.Error;
            }
            else if (level == LogLevel.Warning)
            {
                healthState = HealthState.Warning;
            }

            if (FabricHealerManager.ConfigSettings.TelemetryEnabled && this.telemetryClient != null)
            {
                var telemData = new TelemetryData()
                {
                    Metric = repairAction,
                    ApplicationName = repairConfig?.AppName?.OriginalString ?? string.Empty,
                    ServiceName = repairConfig?.ServiceName?.OriginalString ?? string.Empty,
                    PartitionId = repairConfig?.PartitionId.ToString() ?? string.Empty,
                    ReplicaId = repairConfig?.ReplicaOrInstanceId.ToString() ?? string.Empty,
                    HealthEventDescription = description,
                    HealthState = Enum.GetName(typeof(HealthState), healthState),
                    NodeName = repairConfig?.NodeName ?? string.Empty,
                    Source = $"{(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows_" : "Linux_")}{source}",
                };

                await (this.telemetryClient?.ReportMetricAsync(telemData, token)).ConfigureAwait(false);
            }

            // ETW.
            if (FabricHealerManager.ConfigSettings.EtwEnabled)
            {
                Logger.EtwLogger?.Write(
                    RepairConstants.EventSourceEventName,
                    new
                    {
                        Level = level,
                        Metric = repairAction,
                        ApplicationName = repairConfig?.AppName?.OriginalString ?? string.Empty,
                        ServiceName = repairConfig?.ServiceName?.OriginalString ?? string.Empty,
                        PartitionId = repairConfig?.PartitionId.ToString() ?? string.Empty,
                        ReplicaId = repairConfig?.ReplicaOrInstanceId.ToString() ?? string.Empty,
                        HealthEventDescription = description,
                        HealthState = Enum.GetName(typeof(HealthState), healthState),
                        NodeName = repairConfig?.NodeName ?? string.Empty,
                        Source = source,
                        OSPlatform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux",
                    });
            }

            // Service Fabric HM - Informational Events
            // The Warning/Error events are emitted on the offending node. This is
            // for seeing information in SFX about what happened where vis a vis Repair/Healing.
            var healthReporter = new FabricHealthReporter(this.fabricClient);
            var healthReport = new HealthReport
            {
                Code = repairConfig?.RepairPolicy.RepairId,
                HealthMessage = description,
                NodeName = this.serviceContext.NodeContext.NodeName,
                ReportType = HealthReportType.Node,
                State = healthState,
                HealthReportTimeToLive = TimeSpan.FromMinutes(5),
                Property = "RepairStateInformation",
                Source = source,
            };

            healthReporter.ReportHealthToServiceFabric(healthReport);
        }
    }
}
