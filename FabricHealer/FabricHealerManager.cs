// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Utilities;
using FabricHealer.Repair;
using FabricHealer.Utilities.Telemetry;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HealthReport = FabricHealer.Utilities.HealthReport;
using System.Fabric.Repair;
using System.Fabric.Query;
using FabricHealer.TelemetryLib;
using Octokit;
using System.Fabric.Description;
using System.Runtime.InteropServices;
using static FabricHealer.Repair.RepairTaskManager;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using FabricHealer.Interfaces;
using System.Runtime.CompilerServices;

namespace FabricHealer
{
    public sealed class FabricHealerManager : IDisposable
    {
        private DateTime LastVersionCheckDateTime { get; set; }
        private DateTime LastTelemetrySendDate { get; set; }
        
        // Folks often use their own version numbers. This is for public diagnostic telemetry.
        private const string InternalVersionNumber = "1.2.12";
        private static FabricClient fabricClient;
        private bool disposedValue;
        private bool detectedStopJob;
        private static readonly Uri systemAppUri = new(RepairConstants.SystemAppName);
        private static readonly Uri repairManagerServiceUri = new(RepairConstants.RepairManagerAppName);
        private readonly FabricHealthReporter healthReporter;
        private readonly TimeSpan OperationalTelemetryRunInterval = TimeSpan.FromDays(1);
        private readonly string sfRuntimeVersion;
        private DateTime UtcStartDateTime;
        private static readonly object lockObj = new();
        private static bool IsRmDeployed;

        internal static TelemetryUtilities TelemetryUtilities { get; private set; }
        internal static RepairData RepairHistory { get; private set; }
        internal static int NodeCount { get; private set; }
        internal static long InstanceCount { get; private set; }
        internal static bool IsOneNodeCluster { get; private set; }
        internal static StatelessServiceContext ServiceContext { get; private set; }
        public static Logger RepairLogger { get; private set; }
        public static ConfigSettings ConfigSettings { get; set; }
        public static string CurrentlyExecutingLogicRulesFileName { get; set; }

        // CancellationToken from FabricHealer.RunAsync.
        public static CancellationToken Token { get; private set; }

        /// <summary>
        /// Singleton FabricClient instance used throughout FH. Thread-safe.
        /// </summary>
        public static FabricClient FabricClientSingleton
        {
            get
            {
                if (fabricClient == null)
                {
                    lock (lockObj)
                    {
                        if (fabricClient == null)
                        {
                            fabricClient = new FabricClient();
                            fabricClient.Settings.HealthReportSendInterval = TimeSpan.FromSeconds(1);
                            fabricClient.Settings.HealthReportRetrySendInterval = TimeSpan.FromSeconds(3);
                            return fabricClient;
                        }
                    }
                }
                else
                {
                    try
                    {
                        // This call with throw an ObjectDisposedException if fabricClient was disposed by, say, a plugin or if the runtime
                        // disposed of it for some reason (FO replica restart, for example). This is just a test to ensure it is not in a disposed state.
                        if (fabricClient.Settings.HealthReportSendInterval > TimeSpan.MinValue)
                        {
                            return fabricClient;
                        }
                    }
                    catch (FabricObjectClosedException)
                    {
                        lock (lockObj)
                        {
                            fabricClient.Dispose();
                            fabricClient = null;
                            fabricClient = new FabricClient();
                            fabricClient.Settings.HealthReportSendInterval = TimeSpan.FromSeconds(1);
                            fabricClient.Settings.HealthReportRetrySendInterval = TimeSpan.FromSeconds(3);
                            return fabricClient;
                        }
                    }
                    catch (Exception e) when (e is ObjectDisposedException or InvalidComObjectException)
                    {
                        lock (lockObj)
                        {
                            fabricClient = null;
                            fabricClient = new FabricClient();
                            fabricClient.Settings.HealthReportSendInterval = TimeSpan.FromSeconds(1);
                            fabricClient.Settings.HealthReportRetrySendInterval = TimeSpan.FromSeconds(3);
                            return fabricClient;
                        }
                    }
                }

                return fabricClient;
            }
        }

        /// <summary>
        /// Gets a parameter value from the specified config section or returns supplied default value if 
        /// not specified in config.
        /// </summary>
        /// <param name="sectionName">Name of the section.</param>
        /// <param name="parameterName">Name of the parameter.</param>
        /// <param name="defaultValue">Default value.</param>
        /// <returns>parameter value.</returns>
        public static string GetSettingParameterValue(string sectionName, string parameterName, string defaultValue = null)
        {
            if (string.IsNullOrWhiteSpace(sectionName) || string.IsNullOrWhiteSpace(parameterName))
            {
                return null;
            }

            try
            {
                var serviceConfiguration = ServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config");

                if (serviceConfiguration.Settings.Sections.All(sec => sec.Name != sectionName))
                {
                    return !string.IsNullOrWhiteSpace(defaultValue) ? defaultValue : null;
                }

                if (serviceConfiguration.Settings.Sections[sectionName].Parameters.All(param => param.Name != parameterName))
                {
                    return !string.IsNullOrWhiteSpace(defaultValue) ? defaultValue : null;
                }

                string setting = serviceConfiguration.Settings.Sections[sectionName].Parameters[parameterName]?.Value;

                if (string.IsNullOrWhiteSpace(setting) && defaultValue != null)
                {
                    return defaultValue;
                }

                return setting;
            }
            catch (Exception e) when (e is ArgumentException or KeyNotFoundException)
            {

            }

            return null;
        }

        // This function starts the detection workflow, which involves querying event store for 
        // Warning/Error heath events, looking for well-known FabricObserver error codes with supported
        // repair actions, scheduling and executing related repair tasks.
        public async Task StartAsync()
        {
            UtcStartDateTime = DateTime.UtcNow;

            try
            {
                await InitializeAsync();

                while (true)
                {
                    try
                    {
                        // Try to cancel any FH-owned repair that has exceeded the specified (in logic rule or default)
                        // max execution duration.
                        await TryCleanUpOrphanedFabricHealerRepairJobsAsync();

                        // This can be changed via version-less, parameter-only upgrade of FH. So, don't return when false.
                        if (!ConfigSettings.EnableAutoMitigation)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(15), Token);
                            continue;
                        }

                        // Try and resume any FabricNodeRestart repairs that were abandoned due to FH going down while they were in Approved state.
                        await TryResumeApprovedNodeRestartFHRepairsAsync();

                        // FH can be turned "off" easily by creating a special repair task:
                        // E.g., Start-ServiceFabricRepairTask -NodeNames n/a -CustomAction FabricHealer.Stop
                        // To re-enable FH, you just cancel the FH Stop repair task,
                        // E.g., Stop-ServiceFabricRepairTask -TaskId FabricClient/9baa4d57-45ae-4540-ba45-697b75066424
                        if (!await RepairTaskEngine.HasActiveStopFHRepairJob(CancellationToken.None))
                        {
                            // Existing FabricHealer.Stop repair job was cancelled.
                            if (detectedStopJob)
                            {
                                detectedStopJob = false;
                                string message = $"FabricHealer.Stop repair task has been canceled. FabricHealer will start processing again.";

                                // Using the same Source/Property string values as the related FabricHealer.Stop service healh report
                                // will overwrite the existing Warning health report with a new one with HealthState.Ok (LogLevel.Info).
                                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        RepairConstants.FabricHealer,
                                        message,
                                        CancellationToken.None,
                                        null,
                                        ConfigSettings.EnableVerboseLogging,
                                        TimeSpan.FromMinutes(1),
                                        "FH::Operational::Stop",
                                        EntityType.Service);
                            }

                            // Entity health monitoring entry point.
                            await ProcessHealthEventsAsync();
                        }
                        else
                        {
                            // Don't send this information each time the loop runs. Send it only once.
                            if (!detectedStopJob)
                            {
                                string message = $"Detected active FabricHealer.Stop repair task. FabricHealer will stop processing until this repair task is canceled.";

                                // This will put FabricHealer service entity into Warning state, so you don't forget about this.
                                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Warning,
                                        RepairConstants.FabricHealer,
                                        message,
                                        CancellationToken.None,
                                        null,
                                        ConfigSettings.EnableVerboseLogging,
                                        TimeSpan.MaxValue,
                                        "FH::Operational::Stop",
                                        EntityType.Service);

                                detectedStopJob = true;
                            }
                        }

                        // Identity-agnostic public operational telemetry sent to Service Fabric team (only) for use in
                        // understanding generic behavior of FH in the real world (no PII). This data is sent once a day and will be retained for no more
                        // than 90 days. Please consider enabling this to help the SF team make this technology better.
                        if (ConfigSettings.OperationalTelemetryEnabled && DateTime.UtcNow.Subtract(LastTelemetrySendDate) >= OperationalTelemetryRunInterval)
                        {
                            try
                            {
                                using var telemetryEvents = new TelemetryEvents(ServiceContext);
                                var fhData = GetFabricHealerInternalTelemetryData();

                                if (fhData != null)
                                {
                                    string filepath = Path.Combine(RepairLogger.LogFolderBasePath, $"fh_operational_telemetry.log");

                                    if (telemetryEvents.EmitFabricHealerOperationalEvent(fhData, OperationalTelemetryRunInterval, filepath))
                                    {
                                        LastTelemetrySendDate = DateTime.UtcNow;
                                        ResetInternalDataCounters();
                                    }
                                }
                            }
                            catch (Exception e) when (e is not OutOfMemoryException)
                            {

                            }
                        }

                        // Check for new version once a day.
                        if (DateTime.UtcNow.Subtract(LastVersionCheckDateTime) >= OperationalTelemetryRunInterval)
                        {
                            await CheckGithubForNewVersionAsync();
                            LastVersionCheckDateTime = DateTime.UtcNow;
                        }

                        await Task.Delay(
                            TimeSpan.FromSeconds(
                                ConfigSettings.HealthCheckIntervalInSeconds > 0 ? ConfigSettings.HealthCheckIntervalInSeconds : 10), Token);
                    }
                    catch (AggregateException ae)
                    {
                        RepairLogger.LogWarning("FabricHealerManager handled exception:{0}{1}", Environment.NewLine, ae.Flatten().ToString());
                    }
                    catch (Exception e) when (e is ArgumentException or FabricException or OperationCanceledException or TaskCanceledException or TimeoutException)
                    {
                        // RunAsync token Cancellation occurred while in the loop.
                        if ((e is OperationCanceledException or TaskCanceledException) && Token.IsCancellationRequested)
                        {
                            // Go to outer catch block.
                            throw;
                        }
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or TaskCanceledException)
            {
                // ...and we're done. FabricHealer class will take care of shutdown work as the CancellationToken used is always the RunAsync token.
            }
            catch (Exception e)
            {
                string message = $"Unhandeld Exception in FabricHealerManager:{Environment.NewLine}{e}";
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        RepairConstants.FabricHealer,
                        message,
                        CancellationToken.None,
                        null,
                        ConfigSettings.EnableVerboseLogging);

                // FH Critical Error telemetry (no PII, no user stack) sent to SF team (FabricHealer dev). This information is helpful in understanding what went
                // wrong that lead to the FH process going down (assuming it went down with an exception that can be caught).
                // Please consider enabling this to help the SF team make this technology better.
                if (ConfigSettings.OperationalTelemetryEnabled)
                {
                    try
                    {
                        using var telemetryEvents = new TelemetryEvents(ServiceContext);
                        var fhData = new FabricHealerCriticalErrorEventData
                        {
                            Source = nameof(FabricHealerManager),
                            ErrorMessage = e.Message,
                            ErrorStack = e.StackTrace,
                            CrashTime = DateTime.UtcNow.ToString("o"),
                            Version = InternalVersionNumber,
                            SFRuntimeVersion = sfRuntimeVersion
                        };

                        string filepath = Path.Combine(RepairLogger.LogFolderBasePath, $"fh_critical_error_telemetry.log");
                        _ = telemetryEvents.EmitFabricHealerCriticalErrorEvent(fhData, filepath);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Telemetry is non-critical and should not take down FH.
                        RepairLogger.LogError($"Unable to send operational telemetry: {ex.Message}");
                    }
                }

                await TryCleanUpOrphanedFabricHealerRepairJobsAsync(isClosing: true);
                await TryClearExistingHealthReportsAsync();

                // Don't swallow the exception.
                // Take down FH process. Fix the bug.
                throw;
            }
        }

        /// <summary>
        /// Cancel repair tasks that are executed by FabricHealer which have been running too long.
        /// </summary>
        /// <param name="isClosing">This means ignore the timing constraint and just cancel all active FH-as-executor repairs.</param>
        /// <returns>Task</returns>
        public static async Task TryCleanUpOrphanedFabricHealerRepairJobsAsync(bool isClosing = false)
        {
            if (!IsRmDeployed)
            {
                return;
            }

            try
            {
                RepairTaskList currentFHRepairs =
                    await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                            () => RepairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(
                                    RepairConstants.FHTaskIdPrefix,
                                    CancellationToken.None,
                                    RepairConstants.FabricHealer),
                            CancellationToken.None);

                if (currentFHRepairs == null || !currentFHRepairs.Any())
                {
                    return;
                }

                foreach (RepairTask repair in currentFHRepairs)
                {
                    try
                    {
                        // FH executor data.
                        if (!JsonSerializationUtility.TryDeserializeObject(repair.ExecutorData, out RepairExecutorData exData))
                        {
                            continue;
                        }

                        if (exData?.RepairPolicy == null)
                        {
                            continue;
                        }

                        // Don't cancel node deactivations, unless it has a MaxExecutionTime setting in place.
                        if (exData.RepairPolicy.MaxExecutionTime <= TimeSpan.Zero && 
                            exData.RepairPolicy.RepairAction == RepairActionType.DeactivateNode)
                        {
                            continue;
                        }

                        // This would mean that the job has node-level Impact (like machine repair or restarting a node) and its state is at least Approved.
                        if (exData.RepairPolicy.MaxExecutionTime <= TimeSpan.Zero &&
                            exData.RepairPolicy.RepairAction != RepairActionType.DeactivateNode &&
                            repair.Impact is NodeRepairImpactDescription impact)
                        {
                            if (impact.ImpactedNodes.Any(
                                n => n.NodeName == exData.RepairPolicy.NodeName
                                  && (n.ImpactLevel == NodeImpactLevel.Restart ||
                                      n.ImpactLevel == NodeImpactLevel.RemoveData ||
                                      n.ImpactLevel == NodeImpactLevel.RemoveNode)))
                            {
                                continue;
                            }
                        }

                        // Max execution time check.
                        if (exData.RepairPolicy.MaxExecutionTime > TimeSpan.Zero)
                        {
                            if (repair.CreatedTimestamp.HasValue && DateTime.UtcNow.Subtract(repair.CreatedTimestamp.Value) >= exData.RepairPolicy.MaxExecutionTime)
                            {
                                await FabricRepairTasks.CancelRepairTaskAsync(repair);
                            }
                        }
                        // If this instance of FH is going down while a repair that it owns (FH is executor) is still in flight, then cancel the repair.
                        else if (isClosing && exData.RepairPolicy.RepairAction != RepairActionType.DeactivateNode 
                                 && exData.RepairPolicy.FHRepairExecutorNodeName == ServiceContext.NodeContext.NodeName)
                        {
                            await FabricRepairTasks.CancelRepairTaskAsync(repair);
                        }
                    }
                    catch (AggregateException ae) when (ae.InnerExceptions != null && !ae.InnerExceptions.Any(e => e is OutOfMemoryException))
                    {
                        RepairLogger.LogWarning($"TryCleanUpOrphanedFabricHealerRepairJobs Failure: {ae.Flatten().Message}");
                    }
                    catch (Exception e) when (e is FabricException or InvalidOperationException)
                    {

                    }
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                RepairLogger.LogWarning($"TryCleanUpOrphanedFabricHealerRepairJobs Failure: {e.Message}");
            }
        }

        /// <summary>
        /// Resumes fabric node-level repairs (RestartFabricNode only) that were abandoned due to FH going down while they were in Approved state.
        /// </summary>
        /// <returns>A Task.</returns>
        private static async Task TryResumeApprovedNodeRestartFHRepairsAsync()
        {
            try
            {
                var currentFHApprovedRepairTasksInProgress =
                        await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                () => RepairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(
                                        RepairConstants.FHTaskIdPrefix,
                                        CancellationToken.None,
                                        RepairConstants.FabricHealer,
                                        RepairTaskStateFilter.Approved
                                        ),
                                CancellationToken.None);

                if (currentFHApprovedRepairTasksInProgress == null || currentFHApprovedRepairTasksInProgress.Count == 0)
                {
                    return;
                }

                foreach (var repair in currentFHApprovedRepairTasksInProgress)
                {
                    // Grab the executor data from existing repair.
                    var executorData = repair.ExecutorData;

                    if (string.IsNullOrWhiteSpace(executorData))
                    {
                        continue;
                    }

                    if (!JsonSerializationUtility.TryDeserializeObject(executorData, out RepairExecutorData repairExecutorData))
                    {
                        continue;
                    }

                    if (repairExecutorData?.RepairPolicy == null)
                    {
                        continue;
                    }

                    /* Resume interrupted Fabric Node restart repairs */

                    // There is no need to resume simple repairs that do not require multiple repair steps (e.g., codepackage/process/replica restarts).
                    if (repairExecutorData.RepairPolicy.RepairAction != RepairActionType.RestartFabricNode)
                    {
                        continue;
                    }

                    // The FH owner of the repair task should resume the repair.
                    if (repairExecutorData.RepairPolicy.FHRepairExecutorNodeName != ServiceContext.NodeContext.NodeName)
                    {
                        continue;
                    }

                    // Resuming existing repair flag.
                    repairExecutorData.RepairPolicy.IsResuming = true;
                    TelemetryData repairData = new() 
                    {
                        NodeName = repairExecutorData.RepairPolicy.NodeName,
                        Code = repairExecutorData.RepairPolicy.Code,
                        RepairPolicy = repairExecutorData.RepairPolicy,
                    };
                    repair.ExecutorData = JsonSerializationUtility.TrySerializeObject(repairExecutorData, out string data) ? data : null;

                    // Resume the repair.
                    await ExecuteFabricHealerRepairTaskAsync(repair, repairData, CancellationToken.None);
                }
            }
            catch (Exception e) when (e is not (TaskCanceledException or OperationCanceledException or OutOfMemoryException))
            {
                RepairLogger.LogWarning("Failure in TryResumeFHNodeLevelApprovedRepairsAsync: {0}", e.Message);
            }
        }

        /// <summary>
        /// Processes Service Fabric health events. This is the entry point to SF entity repair.
        /// </summary>
        /// <returns>Task</returns>
        public static async Task ProcessHealthEventsAsync()
        {
            try
            {
                var clusterQueryDesc = new ClusterHealthQueryDescription
                {
                    EventsFilter = new HealthEventsFilter
                    {
                        HealthStateFilterValue = HealthStateFilter.Error | HealthStateFilter.Warning
                    },
                    ApplicationsFilter = new ApplicationHealthStatesFilter
                    {
                        HealthStateFilterValue = HealthStateFilter.Error | HealthStateFilter.Warning
                    },
                    NodesFilter = new NodeHealthStatesFilter
                    {
                        HealthStateFilterValue = HealthStateFilter.Error | HealthStateFilter.Warning
                    },
                    HealthPolicy = new ClusterHealthPolicy(),
                    HealthStatisticsFilter = new ClusterHealthStatisticsFilter
                    {
                        ExcludeHealthStatistics = true,
                        IncludeSystemApplicationHealthStatistics = false
                    }
                };

                ClusterHealth clusterHealth =
                    await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                            () =>
                                FabricClientSingleton.HealthManager.GetClusterHealthAsync(
                                    clusterQueryDesc,
                                    ConfigSettings.AsyncTimeout,
                                    Token),
                            Token);

                if (clusterHealth.AggregatedHealthState == HealthState.Ok)
                {
                    return;
                }

                if (InstanceCount is (-1) or > 1)
                {
                    await RandomWaitAsync(Token);
                }

                // Check cluster upgrade status. If the cluster is upgrading to a new version (or rolling back)
                // then do not attempt any repairs.
                try
                {
                    string udInClusterUpgrade = await UpgradeChecker.GetCurrentUDWhereFabricUpgradeInProgressAsync(Token);

                    if (!string.IsNullOrWhiteSpace(udInClusterUpgrade))
                    {
                        string telemetryDescription = $"Cluster is currently upgrading in UD \"{udInClusterUpgrade}\". " +
                                                      $"Will not schedule or execute repairs at this time.";

                        await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                "MonitorHealthEventsAsync::ClusterUpgradeDetected",
                                telemetryDescription,
                                Token,
                                null,
                                ConfigSettings.EnableVerboseLogging);

                        return;
                    }
                }
                catch (Exception e) when (e is FabricException or TimeoutException)
                {
#if DEBUG
                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "MonitorHealthEventsAsync::HandledException",
                            $"Failure in MonitorHealthEventsAsync::Node:{Environment.NewLine}{e.Message}",
                            Token,
                            null,
                            ConfigSettings.EnableVerboseLogging);
#endif
                }

                // Process Node health.
                if (clusterHealth.NodeHealthStates != null && clusterHealth.NodeHealthStates.Count > 0)
                {
                    if (ConfigSettings.EnableMachineRepair || ConfigSettings.EnableDiskRepair || ConfigSettings.EnableFabricNodeRepair)
                    {
                        try
                        {
                            await ProcessNodeHealthAsync(clusterHealth.NodeHealthStates);
                        }
                        catch (Exception e) when (e is FabricException or TimeoutException)
                        {
#if DEBUG
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    "MonitorHealthEventsAsync::HandledException",
                                    $"Failure in MonitorHealthEventsAsync::Node:{Environment.NewLine}{e.Message}",
                                    Token,
                                    null,
                                    ConfigSettings.EnableVerboseLogging);
#endif
                        }
                    }
                }

                // Process SystemApplication/Service health.
                if (clusterHealth.ApplicationHealthStates != null && clusterHealth.ApplicationHealthStates.Count > 0)
                {
                    if (ConfigSettings.EnableAppRepair || ConfigSettings.EnableSystemAppRepair)
                    {
                        foreach (var app in clusterHealth.ApplicationHealthStates)
                        {
                            if (Token.IsCancellationRequested)
                            {
                                return;
                            }

                            try
                            {
                                // FO/FHProxy system service health report, which is always reported with "fabric:/System" as app name fact.
                                if (app.ApplicationName.OriginalString == RepairConstants.SystemAppName && ConfigSettings.EnableSystemAppRepair)
                                {
                                    await ProcessApplicationHealthAsync(app);
                                }
                                // Could be some system service is in Error/Warning and FO/FHProxy are not the source of that fact. So, ignore fabric:/System/*.
                                else if (!app.ApplicationName.OriginalString.StartsWith(RepairConstants.SystemAppName) && ConfigSettings.EnableAppRepair)
                                {
                                    var appHealth =
                                        await FabricClientSingleton.HealthManager.GetApplicationHealthAsync(
                                                app.ApplicationName,
                                                ConfigSettings.AsyncTimeout,
                                                Token);

                                    if (appHealth.ServiceHealthStates != null && appHealth.ServiceHealthStates.Count > 0 &&
                                        appHealth.ServiceHealthStates.Any(
                                            s => s.AggregatedHealthState is HealthState.Error or HealthState.Warning))
                                    {
                                        foreach (var service in appHealth.ServiceHealthStates.Where(
                                                    s => s.AggregatedHealthState is HealthState.Error or HealthState.Warning))
                                        {
                                            if (Token.IsCancellationRequested)
                                            {
                                                return;
                                            }

                                            if ((InstanceCount is (-1) or > 1) && ConfigSettings.EnableRollingServiceRestarts)
                                            {
                                                await RandomWaitAsync(Token);
                                            }

                                            await ProcessServiceHealthAsync(service);
                                        }
                                    }
                                    else // FO/FHProxy Non-System Application HealthReport.
                                    {
                                        await ProcessApplicationHealthAsync(app);
                                    }
                                }
                            }
                            catch (Exception e) when (e is ArgumentException or FabricException or InvalidOperationException or TimeoutException)
                            {
#if DEBUG
                                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "MonitorHealthEventsAsync::HandledException",
                                        $"Failure in MonitorHealthEventsAsync::Application:{Environment.NewLine}{e.Message}",
                                        Token,
                                        null,
                                        ConfigSettings.EnableVerboseLogging);
#endif
                            }
                        }
                    }
                }
            }
            catch (Exception e) when (e is ArgumentException or FabricException  or TimeoutException)
            {
                // Don't crash..
                RepairLogger.LogWarning($"{e.Message}");
            }
            catch (Exception e) when (e is not OperationCanceledException and not TaskCanceledException)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Error,
                        "MonitorHealthEventsAsync::UnhandledException",
                        $"Failure in MonitorHealthEventsAsync:{Environment.NewLine}{e}",
                        Token,
                        null);

                RepairLogger.LogError($"Unhandled exception in MonitorHealthEventsAsync:{Environment.NewLine}{e}");

                // Fix the bug(s).
                throw;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        public static List<string> ParseRulesFile(string[] rules)
        {
            var repairRules = new List<string>();
            int ptr1 = 0, ptr2 = 0;
            rules = rules.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

            while (ptr1 < rules.Length && ptr2 < rules.Length)
            {
                // Single line comments removal.
                if (rules[ptr2].TrimStart().StartsWith("##"))
                {
                    ptr1++;
                    ptr2++;
                    continue;
                }

                if (rules[ptr2].TrimEnd().EndsWith("."))
                {
                    if (ptr1 == ptr2)
                    {
                        repairRules.Add(rules[ptr2].TrimEnd().Remove(rules[ptr2].Length - 1, 1));
                    }
                    else
                    {
                        string rule = rules[ptr1].Trim();

                        for (int i = ptr1 + 1; i <= ptr2; i++)
                        {
                            rule = rule + ' ' + rules[i].Replace('\t', ' ').Trim();
                        }

                        repairRules.Add(rule.Remove(rule.Length - 1, 1));
                    }
                    ptr2++;
                    ptr1 = ptr2;
                }
                else
                {
                    ptr2++;
                }
            }

            return repairRules;
        }

        public static async Task TryClearExistingHealthReportsAsync()
        {
            var healthReporter = new FabricHealthReporter(RepairLogger);
            var healthReport = new HealthReport
            {
                HealthMessage = "Clearing existing health reports as FabricHealer is starting, stopping or updating.",
                NodeName = ServiceContext.NodeContext.NodeName,
                State = HealthState.Ok,
                HealthReportTimeToLive = TimeSpan.FromMinutes(5),
            };

            // FH.
            try
            {
                var appName = new Uri(RepairConstants.FabricHealerAppName);
                var appHealth =
                    await FabricClientSingleton.HealthManager.GetApplicationHealthAsync(appName, ConfigSettings.AsyncTimeout, CancellationToken.None);
                var FHAppEvents = appHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(RepairConstants.FabricHealer));

                foreach (HealthEvent evt in FHAppEvents)
                {
                    healthReport.AppName = appName;
                    healthReport.Property = evt.HealthInformation.Property;
                    healthReport.SourceId = evt.HealthInformation.SourceId;
                    healthReport.EntityType = EntityType.Application;
                    healthReport.NodeName = ServiceContext.NodeContext.NodeName;

                    healthReporter.ReportHealthToServiceFabric(healthReport);
                    Thread.Sleep(50);
                }
            }
            catch (Exception e) when (e is ArgumentException or FabricException or TimeoutException)
            {

            }

            // Node.
            try
            {
                var nodeHealth =
                    await FabricClientSingleton.HealthManager.GetNodeHealthAsync(ServiceContext.NodeContext.NodeName, ConfigSettings.AsyncTimeout, CancellationToken.None);
                var FHNodeEvents = nodeHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(RepairConstants.FabricHealer));

                foreach (HealthEvent evt in FHNodeEvents)
                {
                    healthReport.Property = evt.HealthInformation.Property;
                    healthReport.SourceId = evt.HealthInformation.SourceId;
                    healthReport.EntityType = EntityType.Node;
                    healthReport.NodeName = ServiceContext.NodeContext.NodeName;

                    healthReporter.ReportHealthToServiceFabric(healthReport);
                    Thread.Sleep(50);
                }
            }
            catch (Exception e) when (e is ArgumentException or FabricException or TimeoutException)
            {

            }
        }

        internal static async Task RandomWaitAsync(CancellationToken token = default)
        {
            var random = new Random();
            int waitTimeMS = random.Next(1000, 10000);
            await Task.Delay(waitTimeMS, token == default ? Token : token);
        }

        public FabricHealerManager(StatelessServiceContext context, CancellationToken token)
        {
            ServiceContext = context;
            Token = token;
            ServiceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent += CodePackageActivationContext_ConfigurationPackageModifiedEvent;
            ConfigSettings = new ConfigSettings(context);
            TelemetryUtilities = new TelemetryUtilities(context);
            RepairLogger = new Logger(RepairConstants.FabricHealer, ConfigSettings.LocalLogPathParameter)
            {
                EnableVerboseLogging = ConfigSettings.EnableVerboseLogging,
                EnableETWLogging = ConfigSettings.EtwEnabled
            };
            RepairHistory = new RepairData();
            healthReporter = new FabricHealthReporter(RepairLogger);
            sfRuntimeVersion = GetServiceFabricRuntimeVersion();
            fabricClient = new FabricClient();
        }

        public static async Task InitializeAsync()
        {
            await TryCancelAbandonedFHRepairsAsync();

            InstanceCount = await GetServiceInstanceCountAsync();
            NodeList nodeList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                        () => FabricClientSingleton.QueryManager.GetNodeListAsync(
                                null, ConfigSettings.AsyncTimeout, CancellationToken.None),
                        CancellationToken.None);

            NodeCount = nodeList.Count;
            IsOneNodeCluster = nodeList.Count == 1;
            string message = $"{repairManagerServiceUri} is deployed.";
            bool isRmDeployed = true;
            var healthReport = new HealthReport
            {
                ServiceName = ServiceContext.ServiceName,
                NodeName = ServiceContext.NodeContext.NodeName,
                EntityType = EntityType.Service,
                HealthMessage = message,
                State = HealthState.Ok,
                Property = "RequirementCheck::RMDeployed",
                HealthReportTimeToLive = TimeSpan.FromDays(1),
                SourceId = RepairConstants.FabricHealer
            };
            ServiceList serviceList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                            () => FabricClientSingleton.QueryManager.GetServiceListAsync(
                                    systemAppUri,
                                    repairManagerServiceUri,
                                    ConfigSettings.AsyncTimeout,
                                    CancellationToken.None),
                            CancellationToken.None);

            if ((serviceList?.Count ?? 0) == 0)
            {
                message =
                    $"FabricHealer Service requires RepairManager service to be deployed in the cluster. " +
                    $"See <a href=\"https://learn.microsoft.com/en-us/azure/service-fabric/service-fabric-cluster-config-upgrade-azure\" target=\"new\">Upgrade Cluster configuration in Azure</a> for more information and guidance.";

                healthReport.HealthMessage = message;
                healthReport.State = HealthState.Warning;
                healthReport.HealthReportTimeToLive = TimeSpan.MaxValue;
                isRmDeployed = false;
            }

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    healthReport.State == HealthState.Warning ? LogLevel.Warning : LogLevel.Info,
                    healthReport.SourceId,
                    healthReport.HealthMessage,
                    CancellationToken.None,
                    telemetryData: null,
                    verboseLogging: true,
                    healthReport.HealthReportTimeToLive,
                    healthReport.Property,
                    healthReport.EntityType);

            IsRmDeployed = isRmDeployed;
        }

        private static async Task<long> GetServiceInstanceCountAsync()
        {
            try
            {
                ServiceDescription serviceDesc =
                    await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                            () => FabricClientSingleton.ServiceManager.GetServiceDescriptionAsync(
                                    ServiceContext.ServiceName,
                                    ConfigSettings.AsyncTimeout,
                                    Token),
                            Token);

                return (serviceDesc as StatelessServiceDescription).InstanceCount;
            }
            catch (Exception e) when (e is FabricException or OperationCanceledException or TaskCanceledException or TimeoutException)
            {
                RepairLogger.LogWarning($"GetServiceInstanceCountAsync failure: {e.Message}");

                // Don't know the answer and it can't be 0 (this code is running).
                return 1;
            }
        }

        private static async Task<bool> IsOneNodeClusterAsync()
        {
            try
            {
                var nodeQueryDesc = new NodeQueryDescription
                {
                    MaxResults = 3,
                };

                NodeList nodes = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () => FabricClientSingleton.QueryManager.GetNodePagedListAsync(
                                                nodeQueryDesc,
                                                ConfigSettings.AsyncTimeout,
                                                Token),
                                         Token);

                return nodes != null && nodes.Count == 1;
            }
            catch (Exception e) when (e is FabricException or TaskCanceledException or TimeoutException)
            {
                RepairLogger.LogWarning($"IsOneNodeClusterAsync failure: {e.Message}");

                // Don't know the answer, so false.
                return false;
            }
        }

        private static void ResetInternalDataCounters()
        {
            RepairHistory.Repairs.Clear();
            RepairHistory.FailedRepairs = 0;
            RepairHistory.SuccessfulRepairs = 0;
            RepairHistory.RepairCount = 0;
            RepairHistory.EnabledRepairCount = 0;
        }

        private FabricHealerOperationalEventData GetFabricHealerInternalTelemetryData()
        {
            FabricHealerOperationalEventData opsTelemData = null;

            try
            {
                RepairHistory.EnabledRepairCount = GetEnabledRepairRuleCount();

                opsTelemData = new FabricHealerOperationalEventData
                {
                    UpTime = DateTime.UtcNow.Subtract(UtcStartDateTime).ToString(),
                    Version = InternalVersionNumber,
                    RepairData = RepairHistory,
                    SFRuntimeVersion = sfRuntimeVersion
                };
            }
            catch
            {

            }

            return opsTelemData;
        }

        private static async Task TryCancelAbandonedFHRepairsAsync()
        {
            try
            {
                RepairTaskList currentFHRepairTasksInProgress =
                    await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                            () => RepairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(
                                    null,
                                    CancellationToken.None,
                                    RepairConstants.FabricHealer),
                            CancellationToken.None);

                if (currentFHRepairTasksInProgress == null || currentFHRepairTasksInProgress.Count == 0)
                {
                    return;
                }

                foreach (var repair in currentFHRepairTasksInProgress)
                {
                    try
                    {
                        // Ignore FH_Infra repairs (like DeactivateNode, where FH is Executor).
                        if (repair.TaskId.StartsWith(RepairConstants.InfraTaskIdPrefix))
                        {
                            continue;
                        }

                        if (repair.State == RepairTaskState.Restoring)
                        {
                            continue;
                        }

                        // Grab the executor data from existing repair.
                        var executorData = repair.ExecutorData;

                        if (string.IsNullOrWhiteSpace(executorData))
                        {
                            continue;
                        }

                        if (!JsonSerializationUtility.TryDeserializeObject(executorData, out RepairExecutorData repairExecutorData, true))
                        {
                            continue;
                        }

                        if (InstanceCount is (-1) or > 1)
                        {
                            // Don't do anything if the orphaned repair was for a different node than this one.
                            if (repairExecutorData.RepairPolicy.NodeName != ServiceContext.NodeContext.NodeName)
                            {
                                continue;
                            }
                        }

                        // Don't cancel node deactivations.
                        if (repairExecutorData.RepairPolicy.RepairAction == RepairActionType.DeactivateNode)
                        {
                            continue;
                        }

                        if (repair.State != RepairTaskState.Completed)
                        {
                            await FabricRepairTasks.CancelRepairTaskAsync(repair);
                        }

                        RepairLogger.LogInfo("Exiting CancelAbandonedFHRepairsAsync: Completed.");
                    }
                    catch (Exception e) when (e is ArgumentException or FabricException or InvalidOperationException or TimeoutException)
                    {
                        RepairLogger.LogWarning($"Handled CancelAbandonedFHRepairsAsync failure: {e.Message}");
                    }
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                RepairLogger.LogWarning($"TryCancelAbandonedFHRepairsAsync: Could not cancel FH repair tasks. Failed with '{e.Message}'");
            }
        }

        private async void CodePackageActivationContext_ConfigurationPackageModifiedEvent(object sender, PackageModifiedEventArgs<ConfigurationPackage> e)
        {
            await TryClearExistingHealthReportsAsync();
            ConfigSettings.UpdateConfigSettings(e.NewPackage.Settings);
        }

        private static async Task ProcessApplicationHealthAsync(ApplicationHealthState appHealthState)
        {
            if (await RepairTaskEngine.HasActiveStopFHRepairJob(Token))
            {
                return;
            }

            ApplicationHealth appHealth = null;
            Uri appName = appHealthState.ApplicationName;

            // System app target? Do not proceed if system app repair is not enabled.
            if (appName.OriginalString.StartsWith(RepairConstants.SystemAppName) && !ConfigSettings.EnableSystemAppRepair)
            {
                return;
            }

            // User app target? Do not proceed if App repair is not enabled.
            if (!appName.OriginalString.StartsWith(RepairConstants.SystemAppName) && !ConfigSettings.EnableAppRepair)
            {
                return;
            }

            appHealth = await FabricClientSingleton.HealthManager.GetApplicationHealthAsync(appName, ConfigSettings.AsyncTimeout, Token);

            if (appName.OriginalString != RepairConstants.SystemAppName)
            {
                try
                {
                    var appUpgradeStatus =
                        await FabricClientSingleton.ApplicationManager.GetApplicationUpgradeProgressAsync(appName, ConfigSettings.AsyncTimeout, Token);

                    if (appUpgradeStatus.UpgradeState is ApplicationUpgradeState.RollingBackInProgress
                        or ApplicationUpgradeState.RollingForwardInProgress
                        or ApplicationUpgradeState.RollingForwardPending)
                    {
                        string udInAppUpgrade = await UpgradeChecker.GetUDWhereApplicationUpgradeInProgressAsync(appName, Token);

                        if (!string.IsNullOrWhiteSpace(udInAppUpgrade))
                        {
                            string udText = $"in UD {udInAppUpgrade}";
                            string telemetryDescription = $"{appName} is upgrading {udText}. Will not attempt application repair at this time.";

                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    "MonitorRepairableHealthEventsAsync::AppUpgradeDetected",
                                    telemetryDescription,
                                    Token,
                                    null,
                                    ConfigSettings.EnableVerboseLogging);
                            return;
                        }
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // App upgrade check failure should not prevent moving forward.
                }
            }

            var healthEvents = appHealth.HealthEvents.Where(
                                s => s.HealthInformation.HealthState is HealthState.Warning
                                or HealthState.Error);

            foreach (var evt in healthEvents)
            {
                Token.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(evt.HealthInformation.Description))
                {
                    continue;
                }

                // If health data is not a serialized TelemetryData instance, then move along.
                if (!JsonSerializationUtility.TryDeserializeObject(evt.HealthInformation.Description, out TelemetryData repairData))
                {
                    continue;
                }

                if (InstanceCount == -1 || InstanceCount > 1)
                {
                    // Randomly wait to decrease chances of simultaneous ownership among FH instances.
                    await RandomWaitAsync(Token);
                }

                if (repairData.Code != null && !SupportedErrorCodes.AppErrorCodesDictionary.ContainsKey(repairData.Code)
                                            || repairData.Code == SupportedErrorCodes.AppErrorNetworkEndpointUnreachable
                                            || repairData.Code == SupportedErrorCodes.AppWarningNetworkEndpointUnreachable)
                {
                    // Network endpoint test failures have no general mitigation.
                    continue;
                }

                // Get configuration settings related to Application (service code package) repair.
                List<string> repairRules;
                string repairId;
                string system = string.Empty;
                bool isRepairInProgress = false;

                if (appName.OriginalString == RepairConstants.SystemAppName)
                {
                    if (!ConfigSettings.EnableSystemAppRepair)
                    {
                        continue;
                    }

                    // Block attempts to schedule Fabric node or system service restart repairs if one is already executing in the cluster.
                    var fhRepairTasks = await RepairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairConstants.FHTaskIdPrefix, Token);

                    if (fhRepairTasks != null && fhRepairTasks.Count > 0)
                    {
                        foreach (var repair in fhRepairTasks)
                        {
                            RepairExecutorData executorData = JsonSerializationUtility.TryDeserializeObject(repair.ExecutorData, out RepairExecutorData exData) ? exData : null;

                            if (executorData?.RepairPolicy?.RepairAction is not RepairActionType.RestartFabricNode and not RepairActionType.RestartProcess)
                            {
                                continue;
                            }

                            string message = $"A Service Fabric System service repair ({repair.TaskId}) is already in progress in the cluster(state: {repair.State}). " +
                                             $"Will not attempt repair at this time.";

                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    source: "ProcessApplicationHealthAsync",
                                    message,
                                    CancellationToken.None,
                                    null,
                                    ConfigSettings.EnableVerboseLogging,
                                    TimeSpan.FromMinutes(5),
                                    property: repair.TaskId);
                            return;
                        }
                    }

                    repairRules = GetRepairRulesForSupportedObserver(RepairConstants.FabricSystemObserver);

                    if (repairRules == null || repairRules?.Count == 0)
                    {
                        continue;
                    }

                    repairId = $"{repairData.NodeName}_{repairData.ProcessName}_{repairData.Code}";
                    system = "System ";

                    if (InstanceCount is (-1) or > 1)
                    {
                        // Randomly wait to decrease chances of simultaneous ownership among FH instances.
                        await RandomWaitAsync(Token);
                    }

                    var currentRepairs =
                        await RepairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairConstants.FHTaskIdPrefix, Token);

                    // Repair already scheduled?
                    if (currentRepairs != null && currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(repairId)))
                    {
                        continue;
                    }
                }
                else
                {
                    if (!ConfigSettings.EnableAppRepair)
                    {
                        continue;
                    }

                    // Don't restart yourself.
                    if (repairData.ServiceName == ServiceContext.ServiceName.OriginalString && repairData.NodeName == ServiceContext.NodeContext.NodeName)
                    {
                        continue;
                    }

                    repairRules = GetRepairRulesForTelemetryData(repairData);

                    // Nothing to do here.
                    if (repairRules == null || repairRules?.Count == 0)
                    {
                        continue;
                    }

                    string serviceId = $"{repairData.ServiceName?.Replace("fabric:/", "").Replace("/", "")}";
                    RepairTaskList currentFHRepairs =
                        await RepairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairConstants.FHTaskIdPrefix, Token);
 
                    // This is the way each FH repair is internally ID'd. This data is stored in the related Repair Task's ExecutorData property.
                    repairId = $"{repairData.NodeName}_{serviceId}_{repairData.Metric?.Replace(" ", string.Empty)}";

                    if (currentFHRepairs != null && currentFHRepairs.Count > 0)
                    {
                        foreach (var repair in currentFHRepairs)
                        {
                            if (!JsonSerializationUtility.TryDeserializeObject(repair.ExecutorData, out RepairExecutorData execData))
                            {
                                continue;
                            }

                            if (execData.RepairPolicy == null)
                            {
                                continue;
                            }

                            // Rolling service restarts.
                            if (ConfigSettings.EnableRollingServiceRestarts
                                && !IsOneNodeCluster
                                && !string.IsNullOrWhiteSpace(execData.RepairPolicy.ServiceName)
                                && execData.RepairPolicy.ServiceName.Equals(repairData.ServiceName, StringComparison.OrdinalIgnoreCase))
                            {
                                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        $"RollingRepairInProgress_{serviceId}_{repair.Target}",
                                        $"There is currently a rolling repair in progress for service {repairData.ServiceName}. " +
                                        $"Target node: {repairData.NodeName}. Current node: {repair.Target}. Repair State: {repair.State}.",
                                        Token,
                                        null);

                                isRepairInProgress = true;
                                break;
                            }

                            // Existing repair for same target in flight.
                            if (!string.IsNullOrWhiteSpace(execData.RepairPolicy.RepairId)
                                && execData.RepairPolicy.RepairId.Equals(repairId, StringComparison.OrdinalIgnoreCase))
                            {
                                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        $"RepairAlreadyInProgress_{serviceId}_{repairData.NodeName}",
                                        $"There is currently a repair in progress for service {repairData.ServiceName} on node " +
                                        $"{repairData.NodeName}. Repair State: {repair.State}.",
                                        Token,
                                        null,
                                        ConfigSettings.EnableVerboseLogging);

                                isRepairInProgress = true;
                                break;
                            }
                        }
                    }
                }

                if (isRepairInProgress)
                {
                    continue;
                }

                /* Start repair workflow */

                repairData.RepairPolicy = new RepairPolicy
                {
                    RepairId = repairId,
                    AppName = repairData.ApplicationName,
                    RepairIdPrefix = RepairConstants.FHTaskIdPrefix,
                    NodeName = repairData.NodeName,
                    Code = repairData.Code,
                    HealthState = repairData.HealthState,
                    ProcessName = repairData.ProcessName,
                    ServiceName = repairData.ServiceName
                };
                repairData.Property = evt.HealthInformation.Property;

                string errOrWarn = "Error";

                if (evt.HealthInformation.HealthState == HealthState.Warning)
                {
                    errOrWarn = "Warning";
                }

                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"ProcessApplicationHealth:{repairId}",
                        $"Detected {errOrWarn} state for Application {repairData.ApplicationName}{Environment.NewLine}" +
                        $"SourceId: {evt.HealthInformation.SourceId}{Environment.NewLine}" +
                        $"Property: {evt.HealthInformation.Property}{Environment.NewLine}" +
                        $"{system}Application repair policy is enabled. " +
                        $"{repairRules.Count} Logic rules found for {system}Application-level repair.",
                        Token,
                        null,
                        ConfigSettings.EnableVerboseLogging);

                HealthEventData eventData = new()
                {
                    Name = repairData.ApplicationName,
                    EntityType = repairData.EntityType,
                    HealthState = repairData.HealthState,
                    LastErrorTransitionAt = evt.LastErrorTransitionAt,
                    LastWarningTransitionAt = evt.LastWarningTransitionAt,
                    SourceId = evt.HealthInformation.SourceId,
                    SourceUtcTimestamp = evt.SourceUtcTimestamp,
                    Property = evt.HealthInformation.Property,
                };

                DetectedHealthEvents.Add(eventData);

                // Start the repair workflow.
                await StartRepairWorkflowAsync(repairData, repairRules, Token);
            }
        }

        private static async Task ProcessServiceHealthAsync(ServiceHealthState serviceHealthState)
        {
            if (await RepairTaskEngine.HasActiveStopFHRepairJob(Token))
            {
                return;
            }

            if (!ConfigSettings.EnableAppRepair)
            {
                return;
            }

            ServiceHealth serviceHealth;
            Uri appName;
            Uri serviceName = serviceHealthState.ServiceName;
            serviceHealth = await FabricClientSingleton.HealthManager.GetServiceHealthAsync(serviceName, ConfigSettings.AsyncTimeout, Token);
            var name = await FabricClientSingleton.QueryManager.GetApplicationNameAsync(serviceName, ConfigSettings.AsyncTimeout, Token);
            appName = name.ApplicationName;

            // User Application upgrade check.
            if (!appName.OriginalString.Contains(RepairConstants.SystemAppName) && !serviceName.OriginalString.Contains(RepairConstants.SystemAppName))
            {
                try
                {
                    ApplicationUpgradeProgress appUpgradeProgress =
                        await FabricClientSingleton.ApplicationManager.GetApplicationUpgradeProgressAsync(appName, ConfigSettings.AsyncTimeout, Token);

                    if (appUpgradeProgress.UpgradeState is ApplicationUpgradeState.RollingBackInProgress
                        or ApplicationUpgradeState.RollingForwardInProgress
                        or ApplicationUpgradeState.RollingForwardPending)
                    {
                        string udInAppUpgrade = await UpgradeChecker.GetUDWhereApplicationUpgradeInProgressAsync(serviceName, Token);

                        if (!string.IsNullOrWhiteSpace(udInAppUpgrade))
                        {
                            string udText = $"in UD {udInAppUpgrade}";
                            string telemetryDescription = $"{appName.OriginalString} is upgrading {udText}. Will not attempt service repair at this time.";

                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    "AppUpgradeDetected",
                                    telemetryDescription,
                                    Token,
                                    null,
                                    ConfigSettings.EnableVerboseLogging);
                            return;
                        }
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // This upgrade check should not prevent moving forward.
                }
            }

            var healthEvents = serviceHealth.HealthEvents.Where(
                e => e.HealthInformation.HealthState is HealthState.Warning or HealthState.Error);

            // Replica repair. This only makes sense if a partition is in Error or Warning state (and Replica repair is still experimental for FH).
            if (ConfigSettings.EnableReplicaRepair)
            {
                var partitionHealthStates = serviceHealth.PartitionHealthStates.Where(
                    p => p.AggregatedHealthState is HealthState.Warning or HealthState.Error);

                if (partitionHealthStates.Any())
                {
                    await ProcessReplicaHealthAsync(serviceHealth);
                    return;
                }
            }

            foreach (var evt in healthEvents)
            {
                Token.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(evt.HealthInformation.Description))
                {
                    continue;
                }

                // If health data is not a serialized instance of a type that implements ITelemetryData, then move along.
                if (!JsonSerializationUtility.TryDeserializeObject(evt.HealthInformation.Description, out TelemetryData repairData))
                {
                    continue;
                }

                // No service name provided. No service.
                if (string.IsNullOrEmpty(repairData.ServiceName))
                {
                    continue;
                }

                if (repairData.ServiceName.ToLower() != serviceName.OriginalString.ToLower())
                {
                    continue;
                }

                if (!RepairExecutor.TryGetGuid(repairData.PartitionId, out _))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(repairData.ApplicationName))
                {
                    repairData.ApplicationName = appName.OriginalString;
                }

                if (InstanceCount is (-1) or > 1)
                {
                    // Randomly wait to decrease chances of simultaneous ownership among FH instances.
                    await RandomWaitAsync(Token);
                }

                // Since FH can run on each node (-1 InstanceCount), if this is the case then have FH only try to repair app services that are also
                // running on the same node.
                if (InstanceCount == -1 && repairData.NodeName != ServiceContext.NodeContext.NodeName)
                {
                    continue;
                }

                if (repairData.Code != null && !SupportedErrorCodes.AppErrorCodesDictionary.ContainsKey(repairData.Code)
                                            || repairData.Code == SupportedErrorCodes.AppErrorNetworkEndpointUnreachable
                                            || repairData.Code == SupportedErrorCodes.AppWarningNetworkEndpointUnreachable)
                {
                    // Network endpoint test failures have no general mitigation.
                    continue;
                }

                // Get configuration settings related to Application (service code package) repair.
                List<string> repairRules;
                string repairId;
                string system = string.Empty;

                // Don't restart thyself.
                if (repairData.ServiceName == ServiceContext.ServiceName.OriginalString && repairData.NodeName == ServiceContext.NodeContext.NodeName)
                {
                    continue;
                }

                repairRules = GetRepairRulesForTelemetryData(repairData);

                if (repairRules == null || repairRules?.Count == 0)
                {
                    continue;
                }

                string serviceId = $"{repairData.ServiceName.Replace("fabric:/", "").Replace("/", "")}";
                var currentFHRepairs =
                    await RepairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairConstants.FHTaskIdPrefix, Token);

                // This is the way each FH repair is ID'd. This data is stored in the related Repair Task's ExecutorData property when FH is executor.
                repairId = $"{repairData.NodeName}_{serviceId}_{repairData.Metric?.Replace(" ", string.Empty)}";
                bool repairInProgress = false;

                if (currentFHRepairs != null && currentFHRepairs.Count > 0)
                {
                    foreach (var repair in currentFHRepairs)
                    {
                        if (!JsonSerializationUtility.TryDeserializeObject(repair.ExecutorData, out RepairExecutorData execData))
                        {
                            continue;
                        }

                        if (execData.RepairPolicy == null)
                        {
                            continue;
                        }
                            
                        // Rolling service restarts.
                        if (ConfigSettings.EnableRollingServiceRestarts 
                            && !IsOneNodeCluster
                            && !string.IsNullOrWhiteSpace(execData.RepairPolicy.ServiceName)
                            && execData.RepairPolicy.ServiceName.Equals(repairData.ServiceName, StringComparison.OrdinalIgnoreCase))
                        {
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    $"RollingRepairInProgress_{serviceId}_{repair.Target}",
                                    $"There is currently a rolling repair in progress for service {repairData.ServiceName}. " +
                                    $"Target node: {repairData.NodeName}. Current node: {repair.Target}. Repair State: {repair.State}.",
                                    Token,
                                    null);

                            repairInProgress = true;
                            break;
                        }

                        // Existing repair for same target in flight.
                        if (!string.IsNullOrWhiteSpace(execData.RepairPolicy.RepairId)
                            && execData.RepairPolicy.RepairId.Equals(repairId, StringComparison.OrdinalIgnoreCase))
                        {
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    $"RepairAlreadyInProgress_{serviceId}_{repairData.NodeName}",
                                    $"There is currently a repair in progress for service {repairData.ServiceName} on node " +
                                    $"{repairData.NodeName}. Repair State: {repair.State}.",
                                    Token,
                                    null,
                                    ConfigSettings.EnableVerboseLogging);
                            
                            repairInProgress = true;
                            break;
                        }
                    }
                }
                
                if (repairInProgress)
                {
                    continue;
                }

                /* Start repair workflow */
                repairData.RepairPolicy = new RepairPolicy
                {
                    RepairId = repairId,
                    AppName = repairData.ApplicationName,
                    RepairIdPrefix = RepairConstants.FHTaskIdPrefix,
                    NodeName = repairData.NodeName,
                    Code = repairData.Code,
                    HealthState = repairData.HealthState,
                    ProcessName = repairData.ProcessName,
                    ServiceName = repairData.ServiceName
                };
                repairData.Property = evt.HealthInformation.Property;

                string errOrWarn = "Error";

                if (evt.HealthInformation.HealthState == HealthState.Warning)
                {
                    errOrWarn = "Warning";
                }

                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"ProcessServiceHealth:{repairId}",
                        $"Detected {errOrWarn} state for Service {repairData.ServiceName}{Environment.NewLine}" +
                        $"SourceId: {evt.HealthInformation.SourceId}{Environment.NewLine}" +
                        $"Property: {evt.HealthInformation.Property}{Environment.NewLine}" +
                        $"{system}Application repair policy is enabled. " +
                        $"{repairRules.Count} Logic rules found for {system}Service-level repair.",
                        Token,
                        null,
                        ConfigSettings.EnableVerboseLogging);

                HealthEventData eventData = new()
                {
                    Name = repairData.ServiceName,
                    EntityType = repairData.EntityType,
                    HealthState = repairData.HealthState,
                    LastErrorTransitionAt = evt.LastErrorTransitionAt,
                    SourceId = evt.HealthInformation.SourceId,
                    SourceUtcTimestamp = evt.SourceUtcTimestamp,
                    Property = evt.HealthInformation.Property
                };

                DetectedHealthEvents.Add(eventData);

                // Start the repair workflow.
                await StartRepairWorkflowAsync(repairData, repairRules, Token);
            }
        }

        private static async Task ProcessNodeHealthAsync(IEnumerable<NodeHealthState> nodeHealthStates)
        {
            if (await RepairTaskEngine.HasActiveStopFHRepairJob(Token))
            {
                RepairLogger.LogInfo("FabricHealer.Stop repair job detected. Exiting ProcessNodeHealthAsync..");
                return;
            }

            foreach (var node in nodeHealthStates)
            {
                Token.ThrowIfCancellationRequested();

                var nodeList = await FabricClientSingleton.QueryManager.GetNodeListAsync(node.NodeName, ConfigSettings.AsyncTimeout, Token);
                string nodeType = nodeList[0].NodeType;
                string nodeUD = nodeList[0].UpgradeDomain;
                NodeStatus nodeStatus = nodeList[0].NodeStatus;
                var nodeHealth = await FabricClientSingleton.HealthManager.GetNodeHealthAsync(node.NodeName, ConfigSettings.AsyncTimeout, Token);
                var nodeHealthEvents =
                    nodeHealth.HealthEvents.Where(
                        s => s.HealthInformation.HealthState is HealthState.Warning or HealthState.Error);

                // Ensure a node in Error is not in Error due to being Down as part of a cluster upgrade or infra update in its UD.
                if (node.AggregatedHealthState == HealthState.Error && nodeStatus == NodeStatus.Down)
                {
                    // Cluster Upgrade in target node's UD?
                    string udInClusterUpgrade = await UpgradeChecker.GetCurrentUDWhereFabricUpgradeInProgressAsync(Token);

                    if (!string.IsNullOrWhiteSpace(udInClusterUpgrade) && udInClusterUpgrade == nodeUD)
                    {
                        string telemetryDescription =
                            $"Cluster is currently upgrading in UD \"{udInClusterUpgrade}\", which is the UD for node {node.NodeName}. " +
                            "Will not schedule a machine repair at this time.";

                        await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                $"{node.NodeName}_Down_ClusterUpgrade",
                                telemetryDescription,
                                Token,
                                null);

                        continue;
                    }

                    // Azure tenant/platform update in progress for the target node?
                    if (await UpgradeChecker.IsAzureJobInProgressAsync(node.NodeName, Token))
                    {
                        string telemetryDescription =
                            $"{node.NodeName} is down due to an Azure Infra repair job (UD = {nodeUD}). Will not schedule a machine repair at this time.";

                        await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                $"{node.NodeName}_Down_AzureInfra",
                                telemetryDescription,
                                Token,
                                null,
                                ConfigSettings.EnableVerboseLogging);

                        continue;
                    }
                }

                foreach (var evt in nodeHealthEvents)
                {
                    Token.ThrowIfCancellationRequested();

                    // Random wait to limit potential duplicate (concurrent) repair job creation from other FH instances.
                    if (InstanceCount is (-1) or > 1)
                    {
                        await RandomWaitAsync(Token);
                    }

                    // Was node health event generated by FO or FHProxy?
                    if (!JsonSerializationUtility.TryDeserializeObject(evt.HealthInformation.Description, out TelemetryData repairData))
                    {
                        if (ConfigSettings.EnableMachineRepair)
                        {
                            // This will enable Machine level repair (reboot, reimage, etc) based on detected SF Node Health Event *not* generated by FO/FHProxy.
                            repairData = new TelemetryData
                            {
                                NodeName = node.NodeName,
                                NodeType = nodeType,
                                EntityType = EntityType.Machine,
                                Description = evt.HealthInformation.Description,
                                HealthState = evt.HealthInformation.HealthState,
                                Property = evt.HealthInformation.Property,
                                Source = evt.HealthInformation.SourceId
                            };
                        }
                    }
                    else
                    {
                        if (repairData.EntityType == EntityType.Unknown)
                        {
                            continue;
                        }

                        // Disk repair?
                        if (repairData.EntityType == EntityType.Disk)
                        {
                            if (!ConfigSettings.EnableDiskRepair)
                            {
                                continue;
                            }

                            await ProcessDiskHealthAsync(evt, repairData);
                            continue;
                        }

                        // Fabric Node repair?
                        if (repairData.EntityType == EntityType.Node)
                        {
                            if (!ConfigSettings.EnableFabricNodeRepair)
                            {
                                continue;
                            }

                            // FabricObserver/FabricHealerProxy-generated health report.
                            await ProcessFabricNodeHealthAsync(evt, repairData);
                            continue;
                        }
                    }

                    // Machine-level repair \\

                    if (!ConfigSettings.EnableMachineRepair)
                    {
                        continue;
                    }

                    // Make sure that there is not already an Infra repair in progress for the target node.
                    if (await RepairTaskEngine.IsNodeRepairCurrentlyInFlightAsync(repairData, Token))
                    {
                        await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                $"{node.NodeName}::MachineRepairAlreadyInProgress",
                                $"There is currently a Machine-level repair in progress for node {node.NodeName}. Will not schedule another repair at this time.",
                                Token,
                                repairData,
                                ConfigSettings.EnableVerboseLogging);

                        continue;
                    }

                    // Get repair rules for supplied facts (TelemetryData).
                    var repairRules = GetRepairRulesForTelemetryData(repairData);

                    if (repairRules == null || repairRules.Count == 0)
                    {
                        continue;
                    }

                    /* Start repair workflow */

                    string repairId = $"MachineRepair_{repairData.NodeName}";
                    repairData.RepairPolicy = new RepairPolicy
                    {
                        RepairId = repairId,
                        RepairIdPrefix = RepairConstants.InfraTaskIdPrefix,
                        NodeName = repairData.NodeName,
                        HealthState = repairData.HealthState,
                        Code = repairData.Code
                    };
                    repairData.Property = evt.HealthInformation.Property;

                    string errOrWarn = "Error";

                    if (evt.HealthInformation.HealthState == HealthState.Warning)
                    {
                        errOrWarn = "Warning";
                    }

                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            repairId,
                            $"Detected Fabric node {repairData.NodeName} is in {errOrWarn}.{Environment.NewLine}" +
                            $"Machine repair target specified. {repairRules.Count} logic rules found for Machine repair.",
                            Token,
                            null,
                            ConfigSettings.EnableVerboseLogging);

                    // Update the in-memory HealthEvent data.
                    HealthEventData eventData = new()
                    {
                        Name = repairData.NodeName,
                        EntityType = repairData.EntityType,
                        HealthState = repairData.HealthState,
                        LastErrorTransitionAt = evt.LastErrorTransitionAt,
                        LastWarningTransitionAt = evt.LastWarningTransitionAt,
                        SourceId = evt.HealthInformation.SourceId,
                        SourceUtcTimestamp = evt.SourceUtcTimestamp,
                        Property= evt.HealthInformation.Property
                    };

                    DetectedHealthEvents.Add(eventData);

                    // Start the repair workflow.
                    await StartRepairWorkflowAsync(repairData, repairRules, Token);
                }
            }
        }

        private static async Task ProcessDiskHealthAsync(HealthEvent evt, TelemetryData repairData)
        {
            if (!ConfigSettings.EnableDiskRepair)
            {
                return;
            }

            if (repairData == null)
            {
                return;
            }

            // Can only repair local disks.
            if (repairData.NodeName != ServiceContext.NodeContext.NodeName)
            {
                return;
            }

            if (await RepairTaskEngine.HasActiveStopFHRepairJob(Token))
            {
                return;
            }

            // Get repair rules for supported source Observer.
            var repairRules = GetRepairRulesForTelemetryData(repairData);

            if (repairRules == null || repairRules.Count == 0)
            {
                return;
            }

            /* Start repair workflow */

            string repairId = $"Disk_Repair_{repairData.Code}{repairData.NodeName}_DeleteFiles";
            repairData.RepairPolicy = new RepairPolicy
            {
                RepairId = repairId,
                NodeName = repairData.NodeName,
                HealthState = repairData.HealthState,
                Code = repairData.Code,
                RepairIdPrefix = RepairConstants.FHTaskIdPrefix
            };
            repairData.Property = evt.HealthInformation.Property;

            string errOrWarn = "Error";

            if (evt.HealthInformation.HealthState == HealthState.Warning)
            {
                errOrWarn = "Warning";
            }

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    repairId,
                    $"Detected {repairData.NodeName} is in {errOrWarn}.{Environment.NewLine}" +
                    $"Disk repair is enabled. {repairRules.Count} logic rules found for Disk repair.",
                    Token,
                    null,
                    ConfigSettings.EnableVerboseLogging);

            HealthEventData eventData = new()
            {
                Name = repairData.NodeName,
                EntityType = repairData.EntityType,
                HealthState = repairData.HealthState,
                LastErrorTransitionAt = evt.LastErrorTransitionAt,
                LastWarningTransitionAt = evt.LastWarningTransitionAt,
                SourceId = evt.HealthInformation.SourceId,
                SourceUtcTimestamp = evt.SourceUtcTimestamp,
                Property = evt.HealthInformation.Property
            };

            DetectedHealthEvents.Add(eventData);

            // Start the repair workflow.
            await StartRepairWorkflowAsync(repairData, repairRules, Token);
        }

        private static async Task ProcessFabricNodeHealthAsync(HealthEvent healthEvent, TelemetryData repairData)
        {
            if (!ConfigSettings.EnableFabricNodeRepair)
            {
                return;
            }

            if (repairData == null)
            {
                return;
            }

            if (InstanceCount is (-1) or > 1)
            {
                await RandomWaitAsync(Token);
            }

            if (await RepairTaskEngine.HasActiveStopFHRepairJob(Token))
            {
                return;
            }

            var repairRules = GetRepairRulesForTelemetryData(repairData);

            if (repairRules == null || repairRules?.Count == 0)
            {
                return;
            }

            string action = repairData.RepairPolicy.RepairAction == RepairActionType.DeactivateNode ? RepairConstants.DeactivateFabricNode : RepairConstants.RestartFabricNode;
            string repairId = $"{repairData.NodeName}_{repairData.NodeType}_{action}";

            var currentRepairs =
                await RepairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairConstants.FHTaskIdPrefix, Token);

            // Block attempts to reschedule another Fabric node-level repair for the same node if a current repair has not yet completed.
            if (currentRepairs != null && currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(repairId)))
            {
                var repair = currentRepairs.FirstOrDefault(r => r.ExecutorData.Contains(repairData.NodeType));
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        repairId,
                        $"There is already a repair in progress for Fabric node {repairData.NodeName}(state: {repair.State})",
                        Token,
                        null,
                        ConfigSettings.EnableVerboseLogging);

                return;
            }

            repairData.RepairPolicy = new RepairPolicy
            {
                RepairId = repairId,
                AppName = repairData.ApplicationName,
                RepairIdPrefix = RepairConstants.FHTaskIdPrefix,
                NodeName = repairData.NodeName,
                Code = repairData.Code,
                HealthState = repairData.HealthState,
                ProcessName = repairData.ProcessName,
                ServiceName = repairData.ServiceName
            };
            repairData.Property = repairData.Property;

            string errOrWarn = "Error";

            if (repairData.HealthState == HealthState.Warning)
            {
                errOrWarn = "Warning";
            }

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    repairId,
                    $"Detected {repairData.NodeName} is in {errOrWarn}.{Environment.NewLine}" +
                    $"Fabric Node repair is enabled. {repairRules.Count} Logic rules found for Fabric Node repair.",
                    Token,
                    null,
                    ConfigSettings.EnableVerboseLogging);

            // Update the in-memory HealthEvent data.
            HealthEventData eventData = new()
            {
                Name = repairData.NodeName,
                EntityType = repairData.EntityType,
                HealthState = repairData.HealthState,
                LastErrorTransitionAt = healthEvent.LastErrorTransitionAt,
                LastWarningTransitionAt = healthEvent.LastWarningTransitionAt,
                SourceId = healthEvent.HealthInformation.SourceId,
                SourceUtcTimestamp = healthEvent.SourceUtcTimestamp,
                Property = healthEvent.HealthInformation.Property
            };

            DetectedHealthEvents.Add(eventData);

            // Start the repair workflow.
            await StartRepairWorkflowAsync(repairData, repairRules, Token);
        }

        private static async Task ProcessReplicaHealthAsync(ServiceHealth serviceHealth)
        {
            // Random wait to limit potential duplicate (concurrent) repair job creation from other FH instances.
            if (InstanceCount is (-1) or > 1)
            {
                await RandomWaitAsync(Token);
            }

            if (await RepairTaskEngine.HasActiveStopFHRepairJob(Token))
            {
                return;
            }

            /*  Example of repairable problem at Replica level, as health event:

                [SourceId] ='System.RAP'
                [Property] = 'IStatefulServiceReplica.ChangeRole(N)Duration'.
                [Description] = The api IStatefulServiceReplica.ChangeRole(N) on node [NodeName] is stuck.
            */

            List<HealthEvent> healthEvents = new();
            var partitionHealthStates = serviceHealth.PartitionHealthStates.Where(
                p => p.AggregatedHealthState is HealthState.Warning or HealthState.Error);

            foreach (var partitionHealthState in partitionHealthStates)
            {
                PartitionHealth partitionHealth =
                    await FabricClientSingleton.HealthManager.GetPartitionHealthAsync(partitionHealthState.PartitionId, ConfigSettings.AsyncTimeout, Token);

                List<ReplicaHealthState> replicaHealthStates = partitionHealth.ReplicaHealthStates.Where(
                    p => p.AggregatedHealthState is HealthState.Warning or HealthState.Error).ToList();

                if (replicaHealthStates != null && replicaHealthStates != null && replicaHealthStates.Count > 0)
                {
                    foreach (var rep in replicaHealthStates)
                    {
                        var replicaHealth =
                            await FabricClientSingleton.HealthManager.GetReplicaHealthAsync(
                                    partitionHealthState.PartitionId,
                                    rep.Id,
                                    ConfigSettings.AsyncTimeout, 
                                    Token);

                        if (replicaHealth != null)
                        {
                            healthEvents = replicaHealth.HealthEvents.Where(
                                h => h.HealthInformation.HealthState is HealthState.Warning or HealthState.Error).ToList();

                            foreach (HealthEvent healthEvent in healthEvents)
                            {
                                if (await RepairTaskEngine.HasActiveStopFHRepairJob(Token))
                                {
                                    return;
                                }

                                if (!healthEvent.HealthInformation.SourceId.Contains("System.RAP"))
                                {
                                    continue;
                                }

                                if (!healthEvent.HealthInformation.Property.Contains("IStatefulServiceReplica.ChangeRole") &&
                                    !healthEvent.HealthInformation.Property.Contains("IReplicator.BuildReplica"))
                                {
                                    continue;
                                }

                                if (!healthEvent.HealthInformation.Description.Contains("is stuck"))
                                {
                                    continue;
                                }

                                var app = await FabricClientSingleton.QueryManager.GetApplicationNameAsync(
                                                    serviceHealth.ServiceName,
                                                    ConfigSettings.AsyncTimeout,
                                                    Token);

                                var replicaList = await FabricClientSingleton.QueryManager.GetReplicaListAsync(
                                                            rep.PartitionId,
                                                            rep.Id,
                                                            ConfigSettings.AsyncTimeout,
                                                            Token);
                                // Replica still exists?
                                if (replicaList.Count == 0)
                                {
                                    continue;
                                }

                                var appName = app?.ApplicationName?.OriginalString;
                                var replica = replicaList[0];
                                var nodeName = replica?.NodeName;

                                // Get configuration settings related to Replica repair. 
                                var repairRules = GetRepairRulesFromConfiguration(RepairConstants.ReplicaRepairPolicySectionName);

                                if (repairRules == null || !repairRules.Any())
                                {
                                    continue;
                                }

                                var repairData = new TelemetryData
                                {
                                    ApplicationName = appName,
                                    EntityType = EntityType.Replica,
                                    HealthState = healthEvent.HealthInformation.HealthState,
                                    NodeName = nodeName,
                                    ReplicaId = rep.Id,
                                    PartitionId = rep.PartitionId.ToString(),
                                    ServiceName = serviceHealth.ServiceName.OriginalString,
                                    Source = RepairConstants.FabricHealerAppName
                                };

                                string repairId = $"{nodeName}_{repairData.PartitionId}_{repairData.ReplicaId}";

                                repairData.RepairPolicy = new RepairPolicy
                                {
                                    RepairId = repairId,
                                    HealthState = repairData.HealthState,
                                    RepairIdPrefix = RepairConstants.FHTaskIdPrefix,
                                    NodeName = nodeName,
                                    AppName = repairData.ApplicationName,
                                    ServiceName = repairData.ServiceName
                                };
                                repairData.Property = healthEvent.HealthInformation.Property;

                                // Repair already in progress?
                                var currentRepairs = await RepairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairConstants.FHTaskIdPrefix, Token);

                                if (currentRepairs != null && currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(repairId)))
                                {
                                    continue;
                                }

                                string errOrWarn = "Error";

                                if (healthEvent.HealthInformation.HealthState == HealthState.Warning)
                                {
                                    errOrWarn = "Warning";
                                }

                                /* Start repair workflow */

                                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        $"ProcessReplicaHealth::Replica_{rep.Id}_{errOrWarn}",
                                        $"Detected Replica {rep.Id} on Partition {rep.PartitionId} is in {errOrWarn}.{Environment.NewLine}" +
                                        $"Replica repair policy is enabled. " +
                                        $"{repairRules.Count} Logic rules found for Replica repair.",
                                        Token,
                                        null,
                                        ConfigSettings.EnableVerboseLogging);

                                // Update the in-memory HealthEvent data.
                                HealthEventData eventData = new()
                                {
                                    Name = repairData.ReplicaId.ToString(),
                                    EntityType = repairData.EntityType,
                                    HealthState = repairData.HealthState,
                                    LastErrorTransitionAt = healthEvent.LastErrorTransitionAt,
                                    LastWarningTransitionAt = healthEvent.LastWarningTransitionAt,
                                    SourceId = healthEvent.HealthInformation.SourceId,
                                    SourceUtcTimestamp = healthEvent.SourceUtcTimestamp,
                                    Property = healthEvent.HealthInformation.Property
                                };

                                DetectedHealthEvents.Add(eventData);

                                // Start the repair workflow.
                                await StartRepairWorkflowAsync(repairData, repairRules, Token);
                            }
                        }
                    }
                }
            }
        }

        private static List<string> GetRepairRulesFromErrorCode(string errorCode, string app = null)
        {
            if (!SupportedErrorCodes.AppErrorCodesDictionary.ContainsKey(errorCode)
                && !SupportedErrorCodes.NodeErrorCodesDictionary.ContainsKey(errorCode))
            {
                return null;
            }

            string repairPolicySectionName;

            switch (errorCode)
            {
                // App repair (user and system).
                case SupportedErrorCodes.AppErrorCpuPercent:
                case SupportedErrorCodes.AppErrorMemoryMB:
                case SupportedErrorCodes.AppErrorMemoryPercent:
                case SupportedErrorCodes.AppErrorTooManyActiveEphemeralPorts:
                case SupportedErrorCodes.AppErrorTooManyActiveTcpPorts:
                case SupportedErrorCodes.AppErrorTooManyOpenHandles:
                case SupportedErrorCodes.AppErrorTooManyThreads:
                case SupportedErrorCodes.AppWarningCpuPercent:
                case SupportedErrorCodes.AppWarningMemoryMB:
                case SupportedErrorCodes.AppWarningMemoryPercent:
                case SupportedErrorCodes.AppWarningTooManyActiveEphemeralPorts:
                case SupportedErrorCodes.AppWarningTooManyActiveTcpPorts:
                case SupportedErrorCodes.AppWarningTooManyOpenHandles:
                case SupportedErrorCodes.AppWarningTooManyThreads:

                    repairPolicySectionName =
                        app == RepairConstants.SystemAppName ? RepairConstants.SystemServiceRepairPolicySectionName : RepairConstants.AppRepairPolicySectionName;
                    break;

                // VM repair.
                case SupportedErrorCodes.NodeErrorCpuPercent:
                case SupportedErrorCodes.NodeErrorMemoryMB:
                case SupportedErrorCodes.NodeErrorMemoryPercent:
                case SupportedErrorCodes.NodeErrorTooManyActiveEphemeralPorts:
                case SupportedErrorCodes.NodeErrorTooManyActiveTcpPorts:
                case SupportedErrorCodes.NodeErrorTotalOpenHandlesPercent:
                case SupportedErrorCodes.NodeWarningCpuPercent:
                case SupportedErrorCodes.NodeWarningMemoryMB:
                case SupportedErrorCodes.NodeWarningMemoryPercent:
                case SupportedErrorCodes.NodeWarningTooManyActiveEphemeralPorts:
                case SupportedErrorCodes.NodeWarningTooManyActiveTcpPorts:
                case SupportedErrorCodes.NodeWarningTotalOpenHandlesPercent:

                    repairPolicySectionName = RepairConstants.MachineRepairPolicySectionName;
                    break;

                // Disk repair.
                case SupportedErrorCodes.NodeWarningDiskSpaceMB:
                case SupportedErrorCodes.NodeErrorDiskSpaceMB:
                case SupportedErrorCodes.NodeWarningDiskSpacePercent:
                case SupportedErrorCodes.NodeErrorDiskSpacePercent:
                case SupportedErrorCodes.NodeWarningFolderSizeMB:
                case SupportedErrorCodes.NodeErrorFolderSizeMB:

                    repairPolicySectionName = RepairConstants.DiskRepairPolicySectionName;
                    break;

                default:
                    return null;
            }

            return GetRepairRulesFromConfiguration(repairPolicySectionName);
        }

        private static List<string> GetRepairRulesForSupportedObserver(string observerName)
        {
            string repairPolicySectionName;

            switch (observerName)
            {
                // App repair (user).
                case RepairConstants.AppObserver:

                    repairPolicySectionName = RepairConstants.AppRepairPolicySectionName;
                    break;

                // System service repair.
                case RepairConstants.FabricSystemObserver:
                    repairPolicySectionName = RepairConstants.SystemServiceRepairPolicySectionName;
                    break;

                // Disk repair
                case RepairConstants.DiskObserver:
                    repairPolicySectionName = RepairConstants.DiskRepairPolicySectionName;
                    break;

                // VM repair.
                case RepairConstants.NodeObserver:

                    repairPolicySectionName = RepairConstants.MachineRepairPolicySectionName;
                    break;

                default:
                    return null;
            }

            return GetRepairRulesFromConfiguration(repairPolicySectionName);
        }

        private static List<string> GetRepairRulesForTelemetryData(TelemetryData repairData)
        {
            string repairPolicySectionName;

            switch (repairData.EntityType)
            {
                // App/Service repair (user).
                case EntityType.Application when repairData.ApplicationName.ToLower() != RepairConstants.SystemAppName.ToLower():
                case EntityType.Service:
                case EntityType.StatefulService:
                case EntityType.StatelessService:
                    repairPolicySectionName = RepairConstants.AppRepairPolicySectionName;
                    break;

                // System service process repair.
                case EntityType.Application when repairData.ProcessName != null:
                case EntityType.Process:
                    repairPolicySectionName = RepairConstants.SystemServiceRepairPolicySectionName;
                    break;

                // Disk repair.
                case EntityType.Disk when ServiceContext.NodeContext.NodeName == repairData.NodeName:
                    repairPolicySectionName = RepairConstants.DiskRepairPolicySectionName;
                    break;

                // Machine repair.
                case EntityType.Machine:
                    repairPolicySectionName = RepairConstants.MachineRepairPolicySectionName;
                    break;

                // Fabric Node repair.
                case EntityType.Node:
                    repairPolicySectionName = RepairConstants.FabricNodeRepairPolicySectionName;
                    break;

                default:
                    return null;
            }

            return GetRepairRulesFromConfiguration(repairPolicySectionName);
        }

        private static List<string> GetRepairRulesFromConfiguration(string repairPolicySectionName)
        {
            try
            {
                string logicRulesConfigFileName = GetSettingParameterValue(repairPolicySectionName, RepairConstants.LogicRulesConfigurationFile);
                string configPath = ServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Path;
                string rulesFolderPath = Path.Combine(configPath, RepairConstants.LogicRulesFolderName);
                string rulesFilePath = Path.Combine(rulesFolderPath, logicRulesConfigFileName);

                if (!File.Exists(rulesFilePath))
                {
                    return null;
                }

                string[] rules = File.ReadAllLines(rulesFilePath);

                if (rules.Length == 0)
                {
                    return null;
                }

                CurrentlyExecutingLogicRulesFileName = logicRulesConfigFileName;
                List<string> repairRules = ParseRulesFile(rules);
                return repairRules;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                return null;
            }
        }

        private static int GetEnabledRepairRuleCount()
        {
            var config = ServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config");
            int count = 0;

            foreach (var section in config.Settings.Sections)
            {
                if (!section.Name.Contains(RepairConstants.RepairPolicy))
                {
                    continue;
                }

                if (section.Parameters[RepairConstants.Enabled]?.Value?.ToLower() == "true")
                {
                    count++;
                }
            }

            return count;
        }

        private void Dispose(bool disposing)
        {
            if (disposedValue)
            {
                return;
            }

            if (disposing)
            {
                fabricClient?.Dispose();
            }

            disposedValue = true;
        }

        private static async Task CheckGithubForNewVersionAsync()
        {
            try
            {
                var githubClient = new GitHubClient(new ProductHeaderValue(RepairConstants.FabricHealer));
                Release latestRelease = await githubClient.Repository.Release.GetLatest("microsoft", "service-fabric-healer");

                if (latestRelease == null)
                {
                    return;
                }

                string releaseAssetName = latestRelease.Name;
                string latestVersion = releaseAssetName.Split(" ")[1];
                Version latestGitHubVersion = new(latestVersion);
                Version localVersion = new(InternalVersionNumber);
                int versionComparison = localVersion.CompareTo(latestGitHubVersion);

                if (versionComparison < 0)
                {
                    string message = $"A newer version of FabricHealer is available: <a href='https://github.com/microsoft/service-fabric-healer/releases' target='_blank'>{latestVersion}</a>";
                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            RepairConstants.FabricHealer,
                            message,
                            Token,
                            null,
                            true,
                            TimeSpan.FromDays(1),
                            "NewVersionAvailable",
                            EntityType.Application);
                }

                latestRelease = null;
                githubClient = null;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Don't take down FO due to error in version check.
                RepairLogger.LogWarning($"Failure in CheckGithubForNewVersionAsync:{Environment.NewLine}{e}");
            }
        }

        private static string GetServiceFabricRuntimeVersion()
        {
            try
            {
                var config = ServiceFabricConfiguration.Instance;
                return config.FabricVersion;
            }
            catch (Exception e) when (e is not (OperationCanceledException or TaskCanceledException))
            {
                RepairLogger.LogWarning($"GetServiceFabricRuntimeVersion failure:{Environment.NewLine}{e}");
            }

            return null;
        }

        internal static bool EnsureProcess(string procName, int procId, DateTime processStartTime)
        {
            if (string.IsNullOrWhiteSpace(procName) || procId < 1)
            {
                return false;
            }

            Process proc = null;

            try
            {
                if (OperatingSystem.IsLinux() && procName.EndsWith(".dll"))
                {
                    Process[] ps = RepairExecutor.GetLinuxDotnetProcessesByFirstArgument(procName);

                    if (ps != null && ps.Length > 0)
                    {
                        proc = ps[0];
                        procName = proc.ProcessName;
                    }
                    else
                    {
                        // no-op. Process not found.
                        RepairLogger.LogWarning(procName + " with id " + procId.ToString() + " not found.");
                        return false;
                    }
                }
                else
                {
                    proc = Process.GetProcessById(procId);
                }

                // ToString() on processStartTime as FO supplies this value as a string. Compare the values directly.
                return proc != null && proc.ProcessName == procName && proc.StartTime.ToString() == processStartTime.ToString();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or SystemException or Win32Exception)
            {
                RepairLogger.LogWarning(procName + " with id " + procId.ToString() + " not found with Exception: " + e.Message);
                _ = TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                $"ProcessApplicationHealth::System{procName}::Exception",
                                procName + " with id " + procId.ToString() + " not found with Exception: " + e.Message,
                                Token,
                                null,
                                ConfigSettings.EnableVerboseLogging);
                return false;
            }
            finally
            {
                proc?.Dispose();
                proc = null;
            }
        }
    }
}