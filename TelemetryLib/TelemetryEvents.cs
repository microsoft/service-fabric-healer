// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Newtonsoft.Json;

namespace FabricHealer.TelemetryLib
{
    /// <summary>
    /// Contains common FabricObserver telemetry events
    /// </summary>
    public class TelemetryEvents : IDisposable
    {
        private const string OperationalEventName = "OperationalEvent";
        private const string CriticalErrorEventName = "CriticalErrorEvent";
        private const string TaskName = "FabricHealer";
        private readonly TelemetryClient telemetryClient;
        private readonly ServiceContext serviceContext;
        private readonly ITelemetryEventSource serviceEventSource;
        private readonly string clusterId, tenantId, clusterType;
        private readonly TelemetryConfiguration appInsightsTelemetryConf;
        private readonly bool isEtwEnabled;

        public TelemetryEvents(
                    FabricClient fabricClient,
                    ServiceContext context,
                    ITelemetryEventSource eventSource,
                    CancellationToken token,
                    bool etwEnabled)
        {
            serviceEventSource = eventSource;
            serviceContext = context;
            appInsightsTelemetryConf = TelemetryConfiguration.CreateDefault();
            appInsightsTelemetryConf.InstrumentationKey = TelemetryConstants.AIKey;
            telemetryClient = new TelemetryClient(appInsightsTelemetryConf);
            var (ClusterId, TenantId, ClusterType) = ClusterIdentificationUtility.TupleGetClusterIdAndTypeAsync(fabricClient, token).GetAwaiter().GetResult();
            clusterId = ClusterId;
            tenantId = TenantId;
            clusterType = ClusterType;
            isEtwEnabled = etwEnabled;
        }

        public bool EmitFabricHealerOperationalEvent(FabricHealerOperationalEventData repairData, TimeSpan runInterval, string logFilePath)
        {
            if (!telemetryClient.IsEnabled())
            {
                return false;
            }

            try
            {
                // ETW
                if (isEtwEnabled)
                {
                    serviceEventSource.InternalFHDataEvent(new { FOInternalTelemtryData = JsonConvert.SerializeObject(repairData) });
                }

                string nodeHashString = string.Empty;
                int nodeNameHash = serviceContext?.NodeContext.NodeName.GetHashCode() ?? -1;

                if (nodeNameHash != -1)
                {
                    nodeHashString = ((uint)nodeNameHash).ToString();
                }

                IDictionary<string, string> eventProperties = new Dictionary<string, string>
                {
                    { "EventName", OperationalEventName},
                    { "TaskName", TaskName},
                    { "EventRunInterval", runInterval.ToString() },
                    { "ClusterId", clusterId },
                    { "ClusterType", clusterType },
                    { "NodeNameHash", nodeHashString },
                    { "FHVersion", repairData.Version },
                    { "UpTime", repairData.UpTime },
                    { "Timestamp", DateTime.UtcNow.ToString("o") },
                    { "OS", repairData.OS }
                };

                if (eventProperties.TryGetValue("ClusterType", out string clustType))
                {
                    if (clustType != TelemetryConstants.ClusterTypeSfrp)
                    {
                        eventProperties.Add("TenantId", tenantId);
                    }
                }

                Dictionary<string, double> eventMetrics = new Dictionary<string, double>
                {
                    { "EnabledRepairCount", repairData.RepairData.EnabledRepairCount },
                    { "TotalRepairAttempts", repairData.RepairData.RepairCount },
                    { "SuccessfulRepairs", repairData.RepairData.SuccessfulRepairs },
                    { "FailedRepairs", repairData.RepairData.FailedRepairs },
                };

                Dictionary<string, double> repairs = repairData.RepairData.Repairs;
                eventMetrics.Append(repairs);

                telemetryClient?.TrackEvent($"{TaskName}.{OperationalEventName}", eventProperties, eventMetrics);
                telemetryClient?.Flush();

                // allow time for flushing
                Thread.Sleep(1000);

                // write a local log file containing the exact information sent to MS \\
                string telemetryData = "{" + string.Join(",", eventProperties.Select(kv => $"\"{kv.Key}\":" + $"\"{kv.Value}\"").ToArray());
                telemetryData += "," + string.Join(",", eventMetrics.Select(kv => $"\"{kv.Key}\":" + kv.Value).ToArray()) + "}";
                _ = TryWriteLogFile(logFilePath, telemetryData);

                eventProperties.Clear();
                eventProperties = null;

                return true;
            }
            catch (Exception e)
            {
                // Telemetry is non-critical and should not take down FH.
                _ = TryWriteLogFile(logFilePath, $"{e}");
            }

            return false;
        }

        public bool EmitFabricHealerCriticalErrorEvent(FabricHealerCriticalErrorEventData fhErrorData, string logFilePath)
        {
            if (!telemetryClient.IsEnabled())
            {
                return false;
            }

            try
            {
                // ETW
                if (isEtwEnabled)
                {
                    serviceEventSource.InternalFHCriticalErrorDataEvent(new { FOCriticalErrorData = JsonConvert.SerializeObject(fhErrorData) });
                }

                string nodeHashString = string.Empty;
                int nodeNameHash = serviceContext?.NodeContext.NodeName.GetHashCode() ?? -1;

                if (nodeNameHash != -1)
                {
                    nodeHashString = ((uint)nodeNameHash).ToString();
                }

                IDictionary<string, string> eventProperties = new Dictionary<string, string>
                {
                    { "EventName", CriticalErrorEventName},
                    { "TaskName", TaskName},
                    { "ClusterId", clusterId },
                    { "ClusterType", clusterType },
                    { "TenantId", tenantId },
                    { "NodeNameHash",  nodeHashString },
                    { "FHVersion", fhErrorData.Version },
                    { "CrashTime", fhErrorData.CrashTime },
                    { "ErrorMessage", fhErrorData.ErrorMessage },
                    { "CrashData", fhErrorData.ErrorStack },
                    { "Timestamp", DateTime.UtcNow.ToString("o") },
                    { "OS", fhErrorData.OS }
                };

                telemetryClient?.TrackEvent($"{TaskName}.{CriticalErrorEventName}", eventProperties);
                telemetryClient?.Flush();

                // allow time for flushing
                Thread.Sleep(1000);

                // write a local log file containing the exact information sent to MS \\
                string telemetryData = "{" + string.Join(",", eventProperties.Select(kv => $"\"{kv.Key}\":" + $"\"{kv.Value}\"").ToArray()) + "}";
                _ = TryWriteLogFile(logFilePath, telemetryData);

                return true;
            }
            catch (Exception e)
            {
                // Telemetry is non-critical and should not take down FH.
                _ = TryWriteLogFile(logFilePath, $"{e}");
            }

            return false;
        }

        public void Dispose()
        {
            telemetryClient?.Flush();

            // allow time for flushing.
            Thread.Sleep(1000);
            appInsightsTelemetryConf?.Dispose();
        }

        const int Retries = 4;

        private bool TryWriteLogFile(string path, string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return false;
            }

            for (var i = 0; i < Retries; i++)
            {
                try
                {
                    string directory = Path.GetDirectoryName(path);

                    if (!Directory.Exists(directory))
                    {
                        if (directory != null)
                        {
                            _ = Directory.CreateDirectory(directory);
                        }
                    }

                    File.WriteAllText(path, content);
                    return true;
                }
                catch
                {

                }

                Thread.Sleep(1000);
            }

            return false;
        }
    }

    public static class Extensions
    {
        public static void Append<K, V>(this Dictionary<K, V> first, Dictionary<K, V> second)
        {
            second.ToList().ForEach(pair => first[pair.Key] = pair.Value);
        }
    }
}