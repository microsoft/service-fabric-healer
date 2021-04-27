// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric.Health;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Interfaces;
using Newtonsoft.Json;

namespace FabricHealer.Utilities.Telemetry
{
    // LogAnalyticsTelemetry class is partially based on public (non-license-protected) sample https://dejanstojanovic.net/aspnet/2018/february/send-data-to-azure-log-analytics-from-c-code/
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
                            HealthScope scope,
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
                    healthScope = scope.ToString(),
                    healthState = state.ToString(),
                    healthEvaluation = unhealthyEvaluations,
                    osPlatform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux",
                    serviceName = serviceName ?? string.Empty,
                    instanceName = instanceName ?? string.Empty,
                });

            await SendTelemetryAsync(jsonPayload, cancellationToken).ConfigureAwait(false);
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

            await SendTelemetryAsync(jsonPayload, cancellationToken).ConfigureAwait(false);
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

            await SendTelemetryAsync(jsonPayload, cancellationToken).ConfigureAwait(false);

            return await Task.FromResult(true).ConfigureAwait(false);
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
        /// <param name="token">CancellatonToken instance.</param>
        /// <returns>A completed task or task containing exception info.</returns>
        private async Task SendTelemetryAsync(string payload, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(WorkspaceId))
            {
                return;
            }

            var requestUri = new Uri($"https://{WorkspaceId}.ods.opinsights.azure.com/api/logs?api-version={ApiVersion}");
            string date = DateTime.UtcNow.ToString("r");
            string signature = GetSignature("POST", payload.Length, "application/json", date, "/api/logs");

            var request = (HttpWebRequest)WebRequest.Create(requestUri);
            request.ContentType = "application/json";
            request.Method = "POST";
            request.Headers["Log-Type"] = LogType;
            request.Headers["x-ms-date"] = date;
            request.Headers["Authorization"] = signature;
            byte[] content = Encoding.UTF8.GetBytes(payload);

            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await using (var requestStreamAsync = await request.GetRequestStreamAsync())
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    await requestStreamAsync.WriteAsync(content, 0, content.Length, token);
                }

                using var responseAsync = await request.GetResponseAsync() as HttpWebResponse;

                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (responseAsync.StatusCode == HttpStatusCode.OK ||
                    responseAsync.StatusCode == HttpStatusCode.Accepted)
                {
                    retries = 0;
                    return;
                }

                logger.LogWarning($"Unexpected response from server in LogAnalyticsTelemetry.SendTelemetryAsync:{Environment.NewLine}{responseAsync.StatusCode}: {responseAsync.StatusDescription}");
            }
            catch (Exception e)
            {
                // An Exception during telemetry data submission should never take down FH process. Log it.
                logger.LogWarning($"Handled Exception in LogAnalyticsTelemetry.SendTelemetryAsync:{Environment.NewLine}{e}");
            }

            if (retries < MaxRetries)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                retries++;
                await Task.Delay(1000, token).ConfigureAwait(false);
                await SendTelemetryAsync(payload, token).ConfigureAwait(false);
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
