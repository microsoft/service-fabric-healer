// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;

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
        private readonly string clusterId, tenantId, clusterType;
        private readonly TelemetryConfiguration appInsightsTelemetryConf;

        public TelemetryEvents(ServiceContext context)
        {
            serviceContext = context;
            appInsightsTelemetryConf = TelemetryConfiguration.CreateDefault();
            appInsightsTelemetryConf.ConnectionString = TelemetryConstants.ConnectionString;
            telemetryClient = new TelemetryClient(appInsightsTelemetryConf);
            clusterId = ClusterInformation.ClusterInfoTuple.ClusterId;
            tenantId = ClusterInformation.ClusterInfoTuple.TenantId;
            clusterType = ClusterInformation.ClusterInfoTuple.ClusterType;
        }

        public bool EmitFabricHealerOperationalEvent(FabricHealerOperationalEventData repairData, TimeSpan runInterval, string logFilePath)
        {
            if (!telemetryClient.IsEnabled())
            {
                return false;
            }

            try
            {
                _ = TryGetHashStringSha256(serviceContext?.NodeContext.NodeName, out string nodeHashString);

                var eventProperties = new Dictionary<string, string>
                {
                    { "EventName", OperationalEventName},
                    { "TaskName", TaskName},
                    { "EventRunInterval", runInterval.ToString() },
                    { "SFRuntimeVersion", repairData.SFRuntimeVersion ?? string.Empty },
                    { "ClusterId", clusterId },
                    { "ClusterType", clusterType },
                    { "NodeNameHash", nodeHashString },
                    { "FHVersion", repairData.Version },
                    { "UpTime", repairData.UpTime },
                    { "Timestamp", DateTime.UtcNow.ToString("o") },
                    { "OS", FabricHealerOperationalEventData.OS }
                };

                if (eventProperties.TryGetValue("ClusterType", out string clustType))
                {
                    if (clustType != TelemetryConstants.ClusterTypeSfrp)
                    {
                        eventProperties.Add("TenantId", tenantId);
                    }
                }

                var eventMetrics = new Dictionary<string, double>
                {
                    { "EnabledRepairCount", repairData.RepairData.EnabledRepairCount },
                    { "TotalRepairAttempts", repairData.RepairData.RepairCount },
                    { "SuccessfulRepairs", repairData.RepairData.SuccessfulRepairs },
                    { "FailedRepairs", repairData.RepairData.FailedRepairs },
                };

                // Add RepairData (repair name, count).
                var repairDataNames = new Dictionary<string, double>();
                foreach (var t in repairData.RepairData.Repairs)
                {
                    if (!repairDataNames.TryAdd(t.Key, t.Value.Count))
                    {
                        repairDataNames[t.Key] = t.Value.Count;
                    }
                }
                eventMetrics.Append(repairDataNames);

                // Add RepairData (source name, count).
                var repairDataSources = new Dictionary<string, double>();
                foreach (var t in repairData.RepairData.Repairs)
                {
                    if (!repairDataSources.TryAdd(t.Value.Source, t.Value.Count))
                    {
                        repairDataSources[t.Value.Source] = t.Value.Count;
                    }
                }
                eventMetrics.Append(repairDataSources);

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
                _ = TryGetHashStringSha256(serviceContext?.NodeContext.NodeName, out string nodeHashString);

                IDictionary<string, string> eventProperties = new Dictionary<string, string>
                {
                    { "EventName", CriticalErrorEventName},
                    { "TaskName", TaskName},
                    { "SFRuntimeVersion", fhErrorData.SFRuntimeVersion ?? string.Empty },
                    { "ClusterId", clusterId },
                    { "ClusterType", clusterType },
                    { "TenantId", tenantId },
                    { "NodeNameHash",  nodeHashString },
                    { "FHVersion", fhErrorData.Version },
                    { "CrashTime", fhErrorData.CrashTime },
                    { "ErrorMessage", fhErrorData.ErrorMessage },
                    { "CrashData", fhErrorData.ErrorStack },
                    { "Timestamp", DateTime.UtcNow.ToString("o") },
                    { "OS", FabricHealerCriticalErrorEventData.OS }
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
            GC.SuppressFinalize(this);
        }

        const int Retries = 4;

        private static bool TryWriteLogFile(string path, string content)
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

        /// <summary>
        /// Tries to compute sha256 hash of a supplied string and converts the hashed bytes to a string supplied in result.
        /// </summary>
        /// <param name="source">The string to be hashed.</param>
        /// <param name="result">The resulting Sha256 hash string. This will be null if the function returns false.</param>
        /// <returns>true if it can compute supplied string to a Sha256 hash and convert result to a string. false if it can't.</returns>
        public static bool TryGetHashStringSha256(string source, out string result)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                result = null;
                return false;
            }

            try
            {
                StringBuilder Sb = new();

                using (var hash = SHA256.Create())
                {
                    Encoding enc = Encoding.UTF8;
                    byte[] byteVal = hash.ComputeHash(enc.GetBytes(source));

                    foreach (byte b in byteVal)
                    {
                        Sb.Append(b.ToString("x2"));
                    }
                }

                result = Sb.ToString();
                return true;
            }
            catch (Exception e) when (e is ArgumentException or EncoderFallbackException or FormatException or ObjectDisposedException)
            {
                result = null;
                return false;
            }
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