﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric.Health;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Interfaces;
using FabricHealer.Repair;
using FabricHealer.TelemetryLib;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace FabricHealer.Utilities.Telemetry
{
    /// <summary>
    /// Abstracts the ApplicationInsights telemetry API calls allowing
    /// other telemetry providers to be plugged in.
    /// </summary>
    public class AppInsightsTelemetry : ITelemetryProvider
    {
        /// <summary>
        /// ApplicationInsights telemetry client.
        /// </summary>
        private readonly TelemetryClient telemetryClient;
        private readonly Logger logger;

        /// <summary>
        /// OBSOLETE: DO NOT USE. Application Insights InstrumentationKey is deprecated. Pass a full ConnectionString to AppInsightsTelemetry constructor instead.
        /// </summary>
        public string Key
        {
            get => throw new NotSupportedException("Key unsupported for ApplicationInsights (InstrumentationKey is deprecated). Use ConnectionString instead.");
            set => throw new NotSupportedException("Key unsupported for ApplicationInsights (InstrumentationKey is deprecated). Use ConnectionString instead.");
        }

        public AppInsightsTelemetry(string connectionString)
        {
            logger = new Logger("TelemetryLog");
            telemetryClient = new TelemetryClient(new TelemetryConfiguration() { ConnectionString = connectionString });
        }

        /// <summary>
        /// Gets a value indicating whether telemetry is enabled or not.
        /// </summary>
        private bool IsEnabled => telemetryClient.IsEnabled() && FabricHealerManager.ConfigSettings.TelemetryEnabled;

        /// <summary>
        /// Calls AI to track the availability.
        /// </summary>
        /// <param name="serviceName">Service name.</param>
        /// <param name="instance">Instance identifier.</param>
        /// <param name="testName">Availability test name.</param>
        /// <param name="captured">The time when the availability was captured.</param>
        /// <param name="duration">The time taken for the availability test to run.</param>
        /// <param name="location">Name of the location the availability test was run from.</param>
        /// <param name="success">True if the availability test ran successfully.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <param name="message">Error message on availability test run failure.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public Task ReportAvailabilityAsync(
                        Uri serviceName,
                        string instance,
                        string testName,
                        DateTimeOffset captured,
                        TimeSpan duration,
                        string location,
                        bool success,
                        CancellationToken cancellationToken,
                        string message = null)
        {
            if (!IsEnabled || cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(1);
            }

            var at = new AvailabilityTelemetry(testName, captured, duration, location, success, message);

            at.Properties.Add("Service", serviceName?.OriginalString);
            at.Properties.Add("Instance", instance);

            telemetryClient.TrackAvailability(at);

            return Task.FromResult(0);
        }

        /// <summary>
        /// Calls AI to report health.
        /// </summary>
        /// <param name="entityType">Scope of health evaluation (Cluster, Node, etc.).</param>
        /// <param name="propertyName">Value of the property.</param>
        /// <param name="state">Health state.</param>
        /// <param name="unhealthyEvaluations">Unhealthy evaluations aggregated description.</param>
        /// <param name="source">Source of emission.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <param name="serviceName">Optional: TraceTelemetry context cloud service name.</param>
        /// <param name="instanceName">Optional: TraceTelemetry context cloud instance name.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public Task ReportHealthAsync(
                        EntityType entityType,
                        string propertyName,
                        HealthState state,
                        string unhealthyEvaluations,
                        string source,
                        CancellationToken cancellationToken,
                        string serviceName = null,
                        string instanceName = null)
        {
            if (!IsEnabled)
            {
                return Task.FromResult(1);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sev = (state == HealthState.Error) ? SeverityLevel.Error
                                    : (state == HealthState.Warning) ? SeverityLevel.Warning : SeverityLevel.Information;

                string healthInfo = string.Empty;

                if (!string.IsNullOrWhiteSpace(unhealthyEvaluations))
                {
                    healthInfo += $"{Environment.NewLine}{unhealthyEvaluations}";
                }

                var tt = new TraceTelemetry($"Service Fabric Health report - {Enum.GetName(typeof(EntityType), entityType)}: {Enum.GetName(typeof(HealthState), state)} -> {source}:{propertyName}{healthInfo}", sev);
                tt.Context.Cloud.RoleName = serviceName;
                tt.Context.Cloud.RoleInstance = instanceName;

                telemetryClient.TrackTrace(tt);
            }
            catch (Exception e)
            {
                logger.LogWarning($"Unhandled exception in TelemetryClient.ReportHealthAsync:{Environment.NewLine}{e.Message}");
                throw;
            }

            return Task.FromResult(0);
        }

        public Task ReportData(string description, LogLevel level, CancellationToken cancellationToken)
        {
            if (!IsEnabled)
            {
                return Task.FromResult(false);
            }

            Dictionary<string, string> properties = new()
            {
                { "ClusterId", ClusterInformation.ClusterInfoTuple.ClusterId },
                { "Message", description },
                { "Source", RepairConstants.FabricHealer },
                { "Level", level.ToString() },
                { "OS", OperatingSystem.IsWindows() ? "Windows" : "Linux" }
            };

            telemetryClient.TrackEvent(RepairConstants.EventSourceEventName, properties);
            return Task.FromResult(true);
        }

        /// <summary>
        /// Sends metrics to a telemetry service.
        /// </summary>
        /// <typeparam name="T">type of data.</typeparam>
        /// <param name="name">name of metric.</param>
        /// <param name="value">value of metric.</param>
        /// <param name="source">source of event.</param>
        /// <param name="cancellationToken">cancellation token.</param>
        /// <returns>A Task of bool.</returns>
        public Task<bool> ReportMetricAsync<T>(
                            string name,
                            T value,
                            string source,
                            CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        /// <summary>
        /// Reports a metric to a telemetry service.
        /// </summary>
        /// <param name="telemetryData">TelemetryData instance.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        public Task ReportMetricAsync(TelemetryData telemetryData, CancellationToken cancellationToken)
        {
            if (telemetryData == null || cancellationToken.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            Dictionary<string, string> properties = new()
            {
                { "Application", telemetryData.ApplicationName },
                { "ServiceName", telemetryData.ServiceName },
                { "SystemServiceProcessName", telemetryData.ProcessName },
                { "ClusterId", telemetryData.ClusterId  },
                { "ErrorCode", telemetryData.Code },
                { "Description", telemetryData.Description },
                { "HealthState", Enum.GetName(typeof(HealthState), telemetryData.HealthState) },
                { "Metric", telemetryData.Metric },
                { "NodeName", telemetryData.NodeName },
                { "ObserverName", telemetryData.ObserverName },
                { "Partition", telemetryData.PartitionId },
                { "Replica", telemetryData.ReplicaId.ToString() },
                { "Source", telemetryData.Source }
            };

            telemetryClient.TrackEvent(RepairConstants.EventSourceEventName, properties);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Calls AI to report a metric.
        /// </summary>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value of the property.</param>
        /// <param name="properties">IDictionary&lt;string&gt;,&lt;string&gt; containing name/value pairs of additional properties.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public Task ReportMetricAsync(
                        string name,
                        long value,
                        IDictionary<string, string> properties,
                        CancellationToken cancellationToken)
        {
            if (!IsEnabled || cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(1);
            }

            _ = telemetryClient.GetMetric(name).TrackValue(value, string.Join(";", properties));

            return Task.FromResult(0);
        }

        /// <summary>
        /// Calls AI to report a metric.
        /// </summary>
        /// <param name="role">Name of the service.</param>
        /// <param name="partition">Guid of the partition.</param>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value if the metric.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public Task ReportMetricAsync(
                        string role,
                        Guid partition,
                        string name,
                        long value,
                        CancellationToken cancellationToken)
        {
            return ReportMetricAsync(role, partition.ToString(), name, value, 1, value, value, value, 0.0, null, cancellationToken);
        }

        /// <summary>
        /// Calls AI to report a metric.
        /// </summary>
        /// <param name="role">Name of the service.</param>
        /// <param name="id">Replica or Instance identifier.</param>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value if the metric.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task ReportMetricAsync(
                            string role,
                            long id,
                            string name,
                            long value,
                            CancellationToken cancellationToken)
        {
            await ReportMetricAsync(role, id.ToString(), name, value, 1, value, value, value, 0.0, null, cancellationToken);
        }

        /// <summary>
        /// Calls AI to report a metric.
        /// </summary>
        /// <param name="roleName">Name of the role. Usually the service name.</param>
        /// <param name="instance">Instance identifier.</param>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value if the metric.</param>
        /// <param name="count">Number of samples for this metric.</param>
        /// <param name="min">Minimum value of the samples.</param>
        /// <param name="max">Maximum value of the samples.</param>
        /// <param name="sum">Sum of all of the samples.</param>
        /// <param name="deviation">Standard deviation of the sample set.</param>
        /// <param name="properties">IDictionary&lt;string&gt;,&lt;string&gt; containing name/value pairs of additional properties.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
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
            if (!IsEnabled)
            {
                return Task.FromResult(false);
            }

            var mt = new MetricTelemetry(name, value)
            {
                Count = count,
                Min = min,
                Max = max,
                StandardDeviation = deviation,
            };

            mt.Context.Cloud.RoleName = roleName;
            mt.Context.Cloud.RoleInstance = instance;

            // Set the properties.
            if (properties != null)
            {
                foreach (var prop in properties)
                {
                    mt.Properties.Add(prop);
                }
            }

            // Track the telemetry.
            telemetryClient.TrackMetric(mt);

            return Task.FromResult(0);
        }
    }
}