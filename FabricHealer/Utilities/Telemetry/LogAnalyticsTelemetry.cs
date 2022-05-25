// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric.Health;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Interfaces;
using Newtonsoft.Json;
using Octokit;

namespace FabricHealer.Utilities.Telemetry
{
    public class LogAnalyticsTelemetry : ITelemetryProvider
    {
        private const int MaxRetries = 5;
        private readonly Logger logger;
        private int retries;

        private string WorkspaceId 
        { 
            get;
        }

        public string Key 
        { 
            get; set; 
        }

        private string ApiVersion 
        { 
            get;
        }

        private string LogType 
        { 
            get;
        }

        public LogAnalyticsTelemetry(
                string workspaceId,
                string sharedKey,
                string logType,
                string apiVersion = "2016-04-01")
        {
            WorkspaceId = workspaceId;
            Key = sharedKey;
            LogType = logType;
            ApiVersion = apiVersion;
            logger = new Logger("TelemetryLogger");
        }

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
                        osPlatform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux",
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

            if (!JsonSerializationUtility.TrySerialize(telemetryData, out string jsonPayload))
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
                        osPlatform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux",
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
        /// <returns>A completed task or task containing exception info.</returns>
        private async Task SendTelemetryAsync(string payload, CancellationToken token)
        {
            if (string.IsNullOrEmpty(WorkspaceId))
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            var requestUri = new Uri($"https://{WorkspaceId}.ods.opinsights.azure.com/api/logs?api-version={ApiVersion}");
            string date = DateTime.UtcNow.ToString("r");
            string signature = GetSignature("POST", payload.Length, "application/json", date, "/api/logs");
            byte[] content = Encoding.UTF8.GetBytes(payload);
            var httpClient = new HttpClient();
            var message = new HttpRequestMessage
            {
                Content = new ByteArrayContent(content),
                Method = HttpMethod.Post,
                RequestUri = requestUri,
            };
            httpClient.DefaultRequestHeaders.Add("ContentType", "application/json");
            httpClient.DefaultRequestHeaders.Add("Log-Type", LogType);
            httpClient.DefaultRequestHeaders.Add("x-ms-date", date);
            httpClient.DefaultRequestHeaders.Add("Authorization", signature);

            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                using HttpResponseMessage response = await httpClient.SendAsync(message, token);

                if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Accepted)
                {
                    retries = 0;
                    return;
                }

                logger.LogWarning(
                    $"Unexpected response from server in LogAnalyticsTelemetry.SendTelemetryAsync:{Environment.NewLine}" +
                    $"{response.StatusCode}: {response.ReasonPhrase}");
            }
            catch (Exception e)
            {
                // An Exception during telemetry data submission should never take down CO process. Log it. Don't throw it. Fix it.
                logger.LogWarning($"Handled Exception in LogAnalyticsTelemetry.SendTelemetryAsync:{Environment.NewLine}{e}");
            }

            if (retries < MaxRetries)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                retries++;
                await Task.Delay(1000, token);
                await SendTelemetryAsync(payload, token);
            }
            else
            {
                // Exhausted retries. Reset counter.
                logger.LogWarning($"Exhausted request retries in LogAnalyticsTelemetry.SendTelemetryAsync: {MaxRetries}. See logs for error details.");
                retries = 0;
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
    }
}
