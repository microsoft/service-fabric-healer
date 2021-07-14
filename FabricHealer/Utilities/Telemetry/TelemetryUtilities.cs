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

            if (!(FabricHealerManager.ConfigSettings is {TelemetryEnabled: true}))
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
        /// <param name="repairConfig">RepairConfiguration instance.</param>
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

            source = source?.Insert(0, "FabricHealer.");

            if (hasRepairInfo)
            {
                repairAction = Enum.GetName(typeof(RepairActionType), repairConfig.RepairPolicy.RepairAction);
            }

            HealthState healthState = level switch
            {
                LogLevel.Error => HealthState.Error,
                LogLevel.Warning => HealthState.Warning,
                _ => HealthState.Ok
            };

            // Service Fabric HM - Health Events (local to cluster).
            var healthReporter = new FabricHealthReporter(fabricClient);
            var healthReport = new HealthReport
            {
                Code = repairConfig?.RepairPolicy.RepairId,
                HealthMessage = description,
                NodeName = serviceContext.NodeContext.NodeName,
                ReportType = HealthReportType.Node,
                State = healthState,
                HealthReportTimeToLive = TimeSpan.FromMinutes(5),
                Property = "RepairStateInformation",
                Source = source,
            };

            healthReporter.ReportHealthToServiceFabric(healthReport);

            /* Telemetry/ETW */

            // Don't log etw or emit telemetry if the events are Informational (Ok Health State) and Verbose Logging is not enabled.
            // This limits noise (and cost) for telemetry service usage.
            if (healthState == HealthState.Ok && !FabricHealerManager.ConfigSettings.EnableVerboseLogging)
            {
                return;
            }

            // Telemetry.
            if (FabricHealerManager.ConfigSettings.TelemetryEnabled && telemetryClient != null)
            {
                var telemData = new TelemetryData()
                {
                    ApplicationName = repairConfig?.AppName?.OriginalString ?? string.Empty,
                    Description = description,
                    HealthState = Enum.GetName(typeof(HealthState), healthState),
                    Metric = repairAction,
                    NodeName = repairConfig?.NodeName ?? string.Empty,
                    PartitionId = repairConfig?.PartitionId.ToString() ?? string.Empty,
                    ReplicaId = repairConfig != null ? repairConfig.ReplicaOrInstanceId : 0,
                    ServiceName = repairConfig?.ServiceName?.OriginalString ?? string.Empty,
                    Source = source,
                    SystemServiceProcessName = repairConfig?.SystemServiceProcessName ?? string.Empty,
                };

                await telemetryClient.ReportMetricAsync(telemData, token).ConfigureAwait(true);
            }

            // ETW.
            if (FabricHealerManager.ConfigSettings.EtwEnabled)
            {
                Logger.EtwLogger?.Write(
                                    RepairConstants.EventSourceEventName,
                                    new
                                    {
                                        ApplicationName = repairConfig?.AppName?.OriginalString ?? string.Empty,
                                        Description = description,
                                        HealthState = Enum.GetName(typeof(HealthState), healthState),
                                        Metric = repairAction,
                                        PartitionId = repairConfig?.PartitionId.ToString() ?? string.Empty,
                                        ReplicaId = repairConfig?.ReplicaOrInstanceId.ToString() ?? string.Empty,
                                        Level = level,
                                        NodeName = repairConfig?.NodeName ?? string.Empty,
                                        OS = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux",
                                        ServiceName = repairConfig?.ServiceName?.OriginalString ?? string.Empty,
                                        Source = source,
                                        SystemServiceProcessName = repairConfig?.SystemServiceProcessName ?? string.Empty,
                                    });
            }
        }
    }
}
