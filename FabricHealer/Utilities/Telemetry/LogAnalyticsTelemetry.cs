﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
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
        private readonly FabricClient fabricClient;
        //private readonly CancellationToken token;
        private readonly Logger logger;

        public string WorkspaceId { get; set; }

        public string Key { get; set; }

        public string ApiVersion { get; set; }

        public string LogType { get; set; }

        public LogAnalyticsTelemetry(
            string workspaceId,
            string sharedKey,
            string logType,
            FabricClient fabricClient,
            string apiVersion = "2016-04-01")
        {
            this.WorkspaceId = workspaceId;
            this.Key = sharedKey;
            this.LogType = logType;
            this.fabricClient = fabricClient;
            this.ApiVersion = apiVersion;
            this.logger = new Logger("TelemetryLogger");
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

            await this.SendTelemetryAsync(jsonPayload).ConfigureAwait(false);
        }

        public async Task ReportMetricAsync(
          TelemetryData telemetryData,
          CancellationToken cancellationToken)
        {
            if (telemetryData == null)
            {
                return;
            }

            string jsonPayload = JsonConvert.SerializeObject(telemetryData);

            await this.SendTelemetryAsync(jsonPayload).ConfigureAwait(false);
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

            await this.SendTelemetryAsync(jsonPayload).ConfigureAwait(false);

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
        /// <returns>A completed task or task containing exception info.</returns>
        private Task SendTelemetryAsync(string payload)
        {
            if (string.IsNullOrEmpty(this.WorkspaceId))
            {
                return Task.CompletedTask;
            }

            var requestUri = new Uri($"https://{this.WorkspaceId}.ods.opinsights.azure.com/api/logs?api-version={this.ApiVersion}");
            string date = DateTime.UtcNow.ToString("r");
            string signature = this.GetSignature("POST", payload.Length, "application/json", date, "/api/logs");

            var request = (HttpWebRequest)WebRequest.Create(requestUri);
            request.ContentType = "application/json";
            request.Method = "POST";
            request.Headers["Log-Type"] = this.LogType;
            request.Headers["x-ms-date"] = date;
            request.Headers["Authorization"] = signature;
            byte[] content = Encoding.UTF8.GetBytes(payload);

            using (var requestStreamAsync = request.GetRequestStream())
            {
                requestStreamAsync.Write(content, 0, content.Length);
            }

            using var responseAsync = (HttpWebResponse)request.GetResponse();
            
            if (responseAsync.StatusCode == HttpStatusCode.OK ||
                responseAsync.StatusCode == HttpStatusCode.Accepted)
            {
                return Task.CompletedTask;
            }

            var responseStream = responseAsync.GetResponseStream();

            if (responseStream == null)
            {
                return Task.CompletedTask;
            }

            using var streamReader = new StreamReader(responseStream);
            string err = $"Exception sending LogAnalytics Telemetry:{Environment.NewLine}{streamReader.ReadToEnd()}";
            this.logger.LogWarning(err);

            return Task.FromException(new Exception(err));
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

            using var encryptor = new HMACSHA256(Convert.FromBase64String(this.Key));

            return $"SharedKey {this.WorkspaceId}:{Convert.ToBase64String(encryptor.ComputeHash(bytes))}";
        }
    }
}
