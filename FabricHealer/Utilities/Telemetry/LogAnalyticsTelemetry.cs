﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric.Health;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Interfaces;
using FabricHealer.Repair;
using FabricHealer.TelemetryLib;
using Newtonsoft.Json;

namespace FabricHealer.Utilities.Telemetry
{
    public class LogAnalyticsTelemetry(
            string workspaceId,
            string sharedKey,
            string logType,
            string apiVersion = "2016-04-01") : ITelemetryProvider
    {
        private readonly Logger logger = new("TelemetryLogger");

        private string WorkspaceId
        {
            get;
        } = workspaceId;

        public string Key
        {
            get; set;
        } = sharedKey;

        private string ApiVersion
        {
            get;
        } = apiVersion;

        private string LogType
        {
            get;
        } = logType;

        private string TargetUri => $"https://{WorkspaceId}.ods.opinsights.azure.com/api/logs?api-version={ApiVersion}";

        public async Task ReportHealthAsync(
                            EntityType entityType,
                            string propertyName,
                            HealthState state,
                            string unhealthyEvaluations,
                            string source,
                            CancellationToken cancellationToken,
                            string serviceName = null,
                            string instanceName = null)
        {
            string jsonPayload = JsonConvert.SerializeObject(
                    new
                    {
                        id = $"FH_{Guid.NewGuid()}",
                        datetime = DateTime.UtcNow,
                        source = "FabricHealer",
                        property = propertyName,
                        healthScope = entityType.ToString(),
                        healthState = state.ToString(),
                        healthEvaluation = unhealthyEvaluations,
                        osPlatform = OperatingSystem.IsWindows() ? "Windows" : "Linux",
                        serviceName = serviceName ?? string.Empty,
                        instanceName = instanceName ?? string.Empty,
                    });

            await SendTelemetryAsync(jsonPayload, cancellationToken);
        }

        public async Task ReportMetricAsync(TelemetryData telemetryData, CancellationToken cancellationToken)
        {
            if (telemetryData == null)
            {
                return;
            }

            if (!JsonSerializationUtility.TrySerializeObject(telemetryData, out string jsonPayload))
            {
                return;
            }

            await SendTelemetryAsync(jsonPayload, cancellationToken);
        }

        public async Task<bool> ReportMetricAsync<T>(
                                    string name,
                                    T value,
                                    string source,
                                    CancellationToken cancellationToken)
        {
            string jsonPayload = JsonConvert.SerializeObject(
                    new
                    {
                        id = $"FH_{Guid.NewGuid()}",
                        datetime = DateTime.UtcNow,
                        source,
                        osPlatform = OperatingSystem.IsWindows() ? "Windows" : "Linux",
                        property = name,
                        value,
                    });

            await SendTelemetryAsync(jsonPayload, cancellationToken);

            return await Task.FromResult(true);
        }

        // Implement functions below as you need.
        public Task ReportAvailabilityAsync(
                        Uri serviceUri,
                        string instance,
                        string testName,
                        DateTimeOffset captured,
                        TimeSpan duration,
                        string location,
                        bool success,
                        CancellationToken cancellationToken,
                        string message = null)
        {
            return Task.CompletedTask;
        }

        public Task ReportMetricAsync(
                        string name,
                        long value,
                        IDictionary<string, string> properties,
                        CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task ReportMetricAsync(
                        string service,
                        Guid partition,
                        string name,
                        long value,
                        CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task ReportMetricAsync(
                        string role,
                        long id,
                        string name,
                        long value,
                        CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task ReportMetricAsync(
                        string roleName,
                        string instance,
                        string name,
                        long value,
                        int count,
                        long min,
                        long max,
                        long sum,
                        double deviation,
                        IDictionary<string, string> properties,
                        CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Sends telemetry data to Azure LogAnalytics via REST.
        /// </summary>
        /// <param name="payload">Json string containing telemetry data.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A completed task or task containing exception info.</returns>
        private async Task SendTelemetryAsync(string payload, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(payload) || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                string date = DateTime.UtcNow.ToString("r");
                string signature = GetSignature("POST", payload.Length, "application/json", date, "/api/logs");
                byte[] content = Encoding.UTF8.GetBytes(payload);
                using HttpClient httpClient = new();
                using HttpRequestMessage request = new(HttpMethod.Post, TargetUri);
                request.Headers.Authorization = AuthenticationHeaderValue.Parse(signature);
                request.Content = new ByteArrayContent(content);
                request.Content.Headers.Add("Content-Type", "application/json");
                request.Content.Headers.Add("Log-Type", LogType);
                request.Content.Headers.Add("x-ms-date", date);
                using var response = await httpClient.SendAsync(request, cancellationToken);

                if (response != null && (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Accepted))
                {
                    return;
                }

                if (response != null)
                {
                    logger.LogWarning(
                        $"Unexpected response from server in LogAnalyticsTelemetry.SendTelemetryAsync:{Environment.NewLine}{response.StatusCode}: {response.ReasonPhrase}");
                }
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException)
            {
                logger.LogInfo($"Exception sending telemetry to LogAnalytics service:{Environment.NewLine}{e.Message}");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Do not take down FO with a telemetry fault. Log it. Warning level will always log.
                // This means there is either a bug in this code or something else that needs your attention.
#if DEBUG
                logger.LogWarning($"Exception sending telemetry to LogAnalytics service:{Environment.NewLine}{e}");
#else
                logger.LogWarning($"Exception sending telemetry to LogAnalytics service: {e.Message}");
#endif
            }
        }

        private string GetSignature(
                        string method,
                        int contentLength,
                        string contentType,
                        string date,
                        string resource)
        {
            string message = $"{method}\n{contentLength}\n{contentType}\nx-ms-date:{date}\n{resource}";
            byte[] bytes = Encoding.UTF8.GetBytes(message);

            using var encryptor = new HMACSHA256(Convert.FromBase64String(Key));

            return $"SharedKey {WorkspaceId}:{Convert.ToBase64String(encryptor.ComputeHash(bytes))}";
        }

        public async Task ReportData(string description, LogLevel level, CancellationToken cancellationToken)
        {
            string jsonPayload = JsonConvert.SerializeObject(
                    new
                    {
                        ClusterInformation.ClusterInfoTuple.ClusterId,
                        Message = description,
                        LogLevel = level.ToString(),
                        Source = RepairConstants.FabricHealer,
                        OS = OperatingSystem.IsWindows() ? "Windows" : "Linux"
                    });

            await SendTelemetryAsync(jsonPayload, cancellationToken);
        }
    }
}
