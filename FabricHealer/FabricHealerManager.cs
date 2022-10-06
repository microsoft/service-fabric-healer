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

namespace FabricHealer
{
    public sealed class FabricHealerManager : IDisposable
    {
        internal static TelemetryUtilities TelemetryUtilities;
        internal static RepairData RepairHistory;

        // Folks often use their own version numbers. This is for internal diagnostic telemetry.
        private const string InternalVersionNumber = "1.1.1.831";
        private static FabricHealerManager singleton;
        private static FabricClient _fabricClient;
        private bool disposedValue;
        private readonly StatelessServiceContext serviceContext;
        private readonly RepairTaskManager repairTaskManager;
        private readonly RepairTaskEngine repairTaskEngine;
        private readonly Uri systemAppUri = new Uri(RepairConstants.SystemAppName);
        private readonly Uri repairManagerServiceUri = new Uri(RepairConstants.RepairManagerAppName);
        private readonly FabricHealthReporter healthReporter;
        private readonly TimeSpan OperationalTelemetryRunInterval = TimeSpan.FromDays(1);
        private readonly string sfRuntimeVersion;
        private int nodeCount;
        private DateTime StartDateTime;
        private long _instanceCount;
        private static readonly object lockObj = new object();

        internal static Logger RepairLogger
        {
            get;
            private set;
        }

        // CancellationToken from FabricHealer.RunAsync.
        internal static CancellationToken Token
        {
            get;
            private set;
        }

        private DateTime LastTelemetrySendDate
        {
            get; set;
        }

        private DateTime LastVersionCheckDateTime
        {
            get; set;
        }

        public static ConfigSettings ConfigSettings
        {
            get; set;
        }

        /// <summary>
        /// Singleton FabricClient instance used throughout FH. Thread-safe.
        /// </summary>
        public static FabricClient FabricClientSingleton
        {
            get
            {
                if (_fabricClient == null)
                {
                    lock (lockObj)
                    {
                        if (_fabricClient == null)
                        {
                            _fabricClient = new FabricClient();
                            _fabricClient.Settings.HealthReportSendInterval = TimeSpan.FromSeconds(1);
                            _fabricClient.Settings.HealthReportRetrySendInterval = TimeSpan.FromSeconds(3);
                            return _fabricClient;
                        }
                    }
                }
                else
                {
                    try
                    {
                        // This call with throw an ObjectDisposedException if fabricClient was disposed by, say, a plugin or if the runtime
                        // disposed of it for some random (unlikely..) reason. This is just a test to ensure it is not in a disposed state.
                        if (_fabricClient.Settings.HealthReportSendInterval > TimeSpan.MinValue)
                        {
                            return _fabricClient;
                        }
                    }
                    catch (Exception e) when (e is ObjectDisposedException || e is InvalidComObjectException)
                    {
                        lock (lockObj)
                        {
                            _fabricClient = null;
                            _fabricClient = new FabricClient();
                            _fabricClient.Settings.HealthReportSendInterval = TimeSpan.FromSeconds(1);
                            _fabricClient.Settings.HealthReportRetrySendInterval = TimeSpan.FromSeconds(3);
                            return _fabricClient;
                        }
                    }
                }

                return _fabricClient;
            }
        }

        private FabricHealerManager(StatelessServiceContext context, CancellationToken token)
        {
            serviceContext = context;
            Token = token;
            serviceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent += CodePackageActivationContext_ConfigurationPackageModifiedEvent;
            ConfigSettings = new ConfigSettings(context);
            TelemetryUtilities = new TelemetryUtilities(context);
            repairTaskEngine = new RepairTaskEngine();
            repairTaskManager = new RepairTaskManager(serviceContext, Token);
            RepairLogger = new Logger(RepairConstants.FabricHealer, ConfigSettings.LocalLogPathParameter)
            {
                EnableVerboseLogging = ConfigSettings.EnableVerboseLogging
            };

            RepairHistory = new RepairData();
            healthReporter = new FabricHealthReporter(RepairLogger);
            sfRuntimeVersion = GetServiceFabricRuntimeVersion();
        }

        /// <summary>
        /// This is the static singleton instance of FabricHealerManager type. FabricHealerManager does not support
        /// multiple instantiations. It does not provide a public constructor.
        /// </summary>
        /// <param name="context">StatelessServiceContext instance.</param>
        /// <param name="token">CancellationToken instance.</param>
        /// <returns>The singleton instance of FabricHealerManager.</returns>
        public static FabricHealerManager Instance(StatelessServiceContext context, CancellationToken token)
        {
            _fabricClient = new FabricClient();
            return singleton ??= new FabricHealerManager(context ?? throw new ArgumentException("ServiceContext can't be null..", nameof(context)), token);
        }

        /// <summary>
        /// Checks if repair manager is enabled in the cluster or not
        /// </summary>
        /// <param name="serviceNameFabricUri"></param>
        /// <param name="cancellationToken">cancellation token to stop the async operation</param>
        /// <returns>true if repair manager application is present in cluster, otherwise false</returns>
        private async Task<bool> InitializeAsync()
        {
            string okMessage = $"{repairManagerServiceUri} is deployed.";
            bool isRmDeployed = true;
            var healthReport = new HealthReport
            {
                NodeName = serviceContext.NodeContext.NodeName,
                AppName = new Uri(RepairConstants.FabricHealerAppName),
                EntityType = EntityType.Application,
                HealthMessage = okMessage,
                State = HealthState.Ok,
                Property = "RequirementCheck::RMDeployed",
                HealthReportTimeToLive = TimeSpan.FromMinutes(5),
                SourceId = RepairConstants.FabricHealer,
            };
            ServiceList serviceList = await FabricClientSingleton.QueryManager.GetServiceListAsync(
                                              systemAppUri,
                                              repairManagerServiceUri,
                                              ConfigSettings.AsyncTimeout,
                                              Token);

            if ((serviceList?.Count ?? 0) == 0)
            {
                string warnMessage =
                    $"{repairManagerServiceUri} could not be found, " +
                    $"FabricHealer Service requires {repairManagerServiceUri} system service to be deployed in the cluster. " +
                    "Consider adding a RepairManager section in your cluster manifest.";

                healthReport.HealthMessage = warnMessage;
                healthReport.State = HealthState.Warning;
                healthReport.Code = SupportedErrorCodes.Ok;
                healthReport.HealthReportTimeToLive = TimeSpan.MaxValue;
                healthReport.SourceId = "CheckRepairManagerDeploymentStatusAsync";
                isRmDeployed = false;
            }

            healthReporter.ReportHealthToServiceFabric(healthReport);

            // Set the service replica instance count (FH is a Stateless singleton service, so it will either be -1 or the number of nodes FH is deployed to).
            _instanceCount = await GetServiceInstanceCountAsync();

            return isRmDeployed;
        }

        private async Task<long> GetServiceInstanceCountAsync()
        {
            ServiceDescription serviceDesc =
                await FabricClientSingleton.ServiceManager.GetServiceDescriptionAsync(serviceContext.ServiceName, ConfigSettings.AsyncTimeout, Token);

            return (serviceDesc as StatelessServiceDescription).InstanceCount;
        }

        /// <summary>
        /// Gets a parameter value from the specified config section or returns supplied default value if 
        /// not specified in config.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="sectionName">Name of the section.</param>
        /// <param name="parameterName">Name of the parameter.</param>
        /// <param name="defaultValue">Default value.</param>
        /// <returns>parameter value.</returns>
        private static string GetSettingParameterValue(
                                StatelessServiceContext context,
                                string sectionName,
                                string parameterName,
                                string defaultValue = null)
        {
            if (string.IsNullOrWhiteSpace(sectionName) || string.IsNullOrWhiteSpace(parameterName))
            {
                return null;
            }

            if (context == null)
            {
                return null;
            }

            try
            {
                var serviceConfiguration = context.CodePackageActivationContext.GetConfigurationPackageObject("Config");

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
            catch (Exception e) when (e is ArgumentException || e is KeyNotFoundException)
            {

            }

            return null;
        }

        // This function starts the detection workflow, which involves querying event store for 
        // Warning/Error heath events, looking for well-known FabricObserver error codes with supported
        // repair actions, scheduling and executing related repair tasks.
        public async Task StartAsync()
        {
            StartDateTime = DateTime.UtcNow;

            if (!ConfigSettings.EnableAutoMitigation)
            {
                return;
            }

            bool initialized = await InitializeAsync();

            if (!initialized)
            {
                return;
            }

            try
            {
                RepairLogger.LogInfo("Starting FabricHealer Health Detection loop.");

                var nodeList =
                   await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                            () => FabricClientSingleton.QueryManager.GetNodeListAsync(null, ConfigSettings.AsyncTimeout, Token),
                            Token);

                nodeCount = nodeList.Count;

                // First, let's clean up any orphan non-node level FabricHealer repair tasks left pending 
                // when the FabricHealer process is killed or otherwise ungracefully closed.
                // This call will return quickly if FH was gracefully closed as there will be
                // no outstanding repair tasks left orphaned.
                await CancelOrResumeAllRunningFHRepairsAsync();

                // Run until RunAsync token is cancelled.
                while (!Token.IsCancellationRequested)
                {
                    if (!ConfigSettings.EnableAutoMitigation)
                    {
                        break;
                    }

                    await MonitorHealthEventsAsync();
                    
                    // Identity-agnostic internal operational telemetry sent to Service Fabric team (only) for use in
                    // understanding generic behavior of FH in the real world (no PII). This data is sent once a day and will be retained for no more
                    // than 90 days. Please consider enabling this to help the SF team make this technology better.
                    if (ConfigSettings.OperationalTelemetryEnabled && DateTime.UtcNow.Subtract(LastTelemetrySendDate) >= OperationalTelemetryRunInterval)
                    {
                        try
                        {
                            using var telemetryEvents = new TelemetryEvents(serviceContext);
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
                        catch
                        {
                            // Telemetry is non-critical and should not take down FH.
                            // TelemetryLib will log exception details to file in top level FH log folder.
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

                RepairLogger.LogInfo("Shutdown signaled. Stopping.");
                await ClearExistingHealthReportsAsync();
            }
            catch (AggregateException)
            {
                // This check is necessary to prevent cancelling outstanding repair tasks if 
                // one of the handled exceptions originated from another operation unrelated to
                // shutdown (like an async operation that timed out).
                if (Token.IsCancellationRequested)
                {
                    RepairLogger.LogInfo("Shutdown signaled. Stopping.");
                    await ClearExistingHealthReportsAsync();
                }
            }
            catch (Exception e) when (e is FabricException || e is OperationCanceledException || e is TaskCanceledException || e is TimeoutException)
            {
                // This check is necessary to prevent cancelling outstanding repair tasks if 
                // one of the handled exceptions originated from another operation unrelated to
                // shutdown (like an async operation that timed out).
                if (Token.IsCancellationRequested)
                {
                    RepairLogger.LogInfo("Shutdown signaled. Stopping.");
                    await ClearExistingHealthReportsAsync();
                }
            }
            catch (Exception e)
            {
                var message = $"Unhandeld Exception in FabricHealerManager:{Environment.NewLine}{e}";
                RepairLogger.LogError(message);
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        RepairConstants.FabricHealer,
                        message,
                        Token,
                        null,
                        ConfigSettings.EnableVerboseLogging);

                // FH Critical Error telemetry (no PII, no user stack) sent to SF team (FabricHealer dev). This information is helpful in understanding what went
                // wrong that lead to the FH process going down (assuming it went down with an exception that can be caught).
                // Please consider enabling this to help the SF team make this technology better.
                if (ConfigSettings.OperationalTelemetryEnabled)
                {
                    try
                    {
                        using var telemetryEvents = new TelemetryEvents(serviceContext);
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
                    catch
                    {
                        // Telemetry is non-critical and should not take down FH.
                    }
                }

                // Don't swallow the exception.
                // Take down FH process. Fix the bug.
                throw;
            }
        }

        private void ResetInternalDataCounters()
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
                    UpTime = DateTime.UtcNow.Subtract(StartDateTime).ToString(),
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

        /// <summary>
        /// Cancels all FabricHealer repair tasks currently in flight (unless in Restoring state).
        /// OR Resumes fabric node-level repairs that were abandoned due to FH going down while they were processing.
        /// </summary>
        /// <returns>A Task.</returns>
        private async Task CancelOrResumeAllRunningFHRepairsAsync()
        {
            try
            {
                var currentFHRepairTasksInProgress =
                        await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                () => repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(
                                        RepairTaskEngine.FabricHealerExecutorName,
                                        Token), 
                                Token);

                if (currentFHRepairTasksInProgress.Count == 0)
                {
                    return;
                }

                foreach (var repair in currentFHRepairTasksInProgress)
                {
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

                    if (!JsonSerializationUtility.TryDeserializeObject(executorData, out RepairExecutorData repairExecutorData))
                    {
                        continue;
                    }

                    // Don't do anything if the orphaned repair was for a different node than this one:
                    if (_instanceCount == -1 && repairExecutorData.RepairData.NodeName != serviceContext.NodeContext.NodeName)
                    {
                        continue;
                    }

                    // Try and cancel existing repair. We may need to create a new one for abandoned repairs where FH goes down for some reason.
                    // Note: CancelRepairTaskAsync handles exceptions (IOE) that may be thrown by RM due to state change policy. 
                    // The repair state could change to Completed after this call is made, for example, and before RM API call.
                    if (repair.State != RepairTaskState.Completed)
                    {
                        await FabricRepairTasks.CancelRepairTaskAsync(repair);
                    }

                    /* Resume interrupted Fabric Node restart repairs */

                    // There is no need to resume simple repairs that do not require multiple repair steps (e.g., codepackage/process/replica restarts).
                    if (repairExecutorData.RepairData.RepairPolicy.RepairAction != RepairActionType.RestartFabricNode)
                    {
                        continue;
                    }

                    string errorCode = repairExecutorData.RepairData.Code;
                    
                    if (string.IsNullOrWhiteSpace(errorCode))
                    {
                        continue;
                    }

                    // File Deletion repair is a node-level (VM) repair, but is not multi-step. Ignore.
                    if (SupportedErrorCodes.GetCodeNameFromErrorCode(errorCode).Contains("Disk")
                        || repairExecutorData.RepairData.RepairPolicy.RepairAction == RepairActionType.DeleteFiles)
                    {
                        continue;
                    }

                    // Fabric System service warnings/errors from FO can be Node level repair targets (e.g., Fabric binary needs to be restarted).
                    // FH will restart the node hosting the troubled SF system service if specified in related logic rules.
                    var repairRules =
                        GetRepairRulesFromConfiguration(
                            !string.IsNullOrWhiteSpace(
                                repairExecutorData.RepairData.ProcessName) ? RepairConstants.SystemServiceRepairPolicySectionName : RepairConstants.FabricNodeRepairPolicySectionName);

                    var repairData = new TelemetryData
                    {
                        NodeName = repairExecutorData.RepairData.NodeName,
                        Code = errorCode,
                    };

                    await repairTaskManager.RunGuanQueryAsync(repairData, repairRules, repairExecutorData);
                }
            }
            catch (Exception e) when (e is FabricException || e is OperationCanceledException || e is TaskCanceledException)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "CancelOrResumeAllRunningFHRepairsAsync",
                        $"Could not cancel or resume repair tasks. Failed with:{Environment.NewLine}{e}",
                        Token,
                        null,
                        ConfigSettings.EnableVerboseLogging);
            }
        }

        private async void CodePackageActivationContext_ConfigurationPackageModifiedEvent(object sender, PackageModifiedEventArgs<ConfigurationPackage> e)
        {
            await ClearExistingHealthReportsAsync();
            ConfigSettings.UpdateConfigSettings(e.NewPackage.Settings);
        }

        /* Potential TODOs. This list should grow and external predicates should be written to support related workflow composition in logic rule file(s).

            Symptom                                                 Mitigation 
            ------------------------------------------------------  ---------------------------------------------------
            Expired Certificate [TP Scenario]	                    Modify the cluster manifest AEPCC to true (we already have an automation script for this scenario)
            Node crash due to lease issue 	                        Restart the neighboring VM
            Node Crash due to slow network issue	                Restart the VM
            System Service in quorum loss	                        Repair the partition/Restart the VM
            Node stuck in disabling state due to MR [safety check]	Address safety issue through automation
            [MR Scenario] Node in down state: MR unable 
            to send the Remove-ServiceFabricNodeState in time	    Remove-ServiceFabricNodeState
            Unused container fill the disk space	                Call docker prune cmd 
            Primary replica for system service in IB state forever	Restart the primary replica 
        */

        private async Task MonitorHealthEventsAsync()
        {
            try
            {
                var clusterHealth = await FabricClientSingleton.HealthManager.GetClusterHealthAsync(ConfigSettings.AsyncTimeout, Token);

                if (clusterHealth.AggregatedHealthState == HealthState.Ok)
                {
                    return;
                }

                // Check cluster upgrade status. If the cluster is upgrading to a new version (or rolling back)
                // then do not attempt any repairs.
                try
                {
                    string udInClusterUpgrade = await UpgradeChecker.GetCurrentUDWhereFabricUpgradeInProgressAsync(Token);

                    if (!string.IsNullOrWhiteSpace(udInClusterUpgrade))
                    {
                        string telemetryDescription = $"Cluster is currently upgrading in UD \"{udInClusterUpgrade}\". Will not schedule or execute repairs at this time.";

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
                catch (Exception e) when (e is FabricException || e is TimeoutException)
                {
#if DEBUG
                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            "MonitorHealthEventsAsync::HandledException",
                            $"Failure in MonitorHealthEventsAsync::Node:{Environment.NewLine}{e}",
                            Token,
                            null,
                            ConfigSettings.EnableVerboseLogging);
#endif
                }

                var unhealthyEvaluations = clusterHealth.UnhealthyEvaluations;

                foreach (var evaluation in unhealthyEvaluations)
                {
                    Token.ThrowIfCancellationRequested();

                    string kind = Enum.GetName(typeof(HealthEvaluationKind), evaluation.Kind);

                    if (kind != null && kind.Contains("Node"))
                    {
                        if (!ConfigSettings.EnableMachineRepair && !ConfigSettings.EnableDiskRepair && !ConfigSettings.EnableFabricNodeRepair)
                        {
                            continue;
                        }

                        try
                        {
                            await ProcessNodeHealthAsync(clusterHealth.NodeHealthStates);
                        }
                        catch (Exception e) when (e is FabricException || e is TimeoutException)
                        {
#if DEBUG
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    "MonitorHealthEventsAsync::HandledException",
                                    $"Failure in MonitorHealthEventsAsync::Node:{Environment.NewLine}{e}",
                                    Token,
                                    null,
                                    ConfigSettings.EnableVerboseLogging);
#endif
                        }
                    }
                    else if (kind != null && kind.Contains("Application"))
                    {
                        if (!ConfigSettings.EnableAppRepair && !ConfigSettings.EnableSystemAppRepair)
                        {
                            continue;
                        }

                        foreach (var app in clusterHealth.ApplicationHealthStates)
                        {
                            Token.ThrowIfCancellationRequested();

                            try
                            {
                                var entityHealth =
                                    await FabricClientSingleton.HealthManager.GetApplicationHealthAsync(app.ApplicationName, ConfigSettings.AsyncTimeout, Token);

                                if (app.AggregatedHealthState == HealthState.Ok)
                                {
                                    continue;
                                }

                                if (entityHealth.ServiceHealthStates != null && entityHealth.ServiceHealthStates.Any(
                                        s => s.AggregatedHealthState == HealthState.Error || s.AggregatedHealthState == HealthState.Warning))
                                {
                                    foreach (var service in entityHealth.ServiceHealthStates.Where(
                                                    s => s.AggregatedHealthState == HealthState.Error || s.AggregatedHealthState == HealthState.Warning))
                                    {
                                        await ProcessServiceHealthAsync(service);
                                    }
                                }
                                else
                                {
                                    await ProcessApplicationHealthAsync(app);
                                }
                            }
                            catch (Exception e) when (e is FabricException || e is TimeoutException)
                            {
#if DEBUG
                                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "MonitorHealthEventsAsync::HandledException",
                                        $"Failure in MonitorHealthEventsAsync::Application:{Environment.NewLine}{e}",
                                        Token,
                                        null,
                                        ConfigSettings.EnableVerboseLogging);
#endif
                            }
                        }
                    }
                }
            }
            catch (Exception e) when (e is ArgumentException || e is FabricException)
            {
                // Don't crash.
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Error,
                        "MonitorHealthEventsAsync::UnhandledException",
                        $"Failure in MonitorHealthEventsAsync:{Environment.NewLine}{e}",
                        Token,
                        null,
                        ConfigSettings.EnableVerboseLogging);

                RepairLogger.LogWarning($"Unhandled exception in MonitorHealthEventsAsync:{Environment.NewLine}{e}");

                // Fix the bug(s).
                throw;
            }
        }

        /// <summary>
        /// Processes SF Application health events, including System Application events (which could have Fabric Node impact repairs).
        /// </summary>
        /// <param name="appHealthStates">Collection of ApplicationHealthState objects.</param>
        /// <returns>A task.</returns>
        private async Task ProcessApplicationHealthAsync(ApplicationHealthState appHealthState)
        {
            ApplicationHealth appHealth = null;
            Uri appName = appHealthState.ApplicationName;

            // System app target? Do not proceed if system app repair is not enabled.
            if (appName.OriginalString == RepairConstants.SystemAppName && !ConfigSettings.EnableSystemAppRepair)
            {
                return;
            }

            // User app target? Do not proceed if App repair is not enabled.
            if (appName.OriginalString != RepairConstants.SystemAppName && !ConfigSettings.EnableAppRepair)
            {
                return;
            }

            appHealth = await FabricClientSingleton.HealthManager.GetApplicationHealthAsync(appName, ConfigSettings.AsyncTimeout, Token);

            if (appName.OriginalString != RepairConstants.SystemAppName)
            {
                try
                {
                    var appUpgradeStatus = await FabricClientSingleton.ApplicationManager.GetApplicationUpgradeProgressAsync(appName);

                    if (appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingBackInProgress
                        || appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingForwardInProgress
                        || appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingForwardPending)
                    {
                        List<int> udInAppUpgrade = await UpgradeChecker.GetUdsWhereApplicationUpgradeInProgressAsync(appName, Token);
                        string udText = string.Empty;

                        // -1 means no upgrade in progress for application.
                        if (udInAppUpgrade.Any(ud => ud > -1))
                        {
                            udText = $"in UD {udInAppUpgrade.First(ud => ud > -1)}";
                        }

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
                catch (FabricException)
                {
                    // This upgrade check should not prevent moving forward if the fabric client call fails with an FE.
                }
            }

            var healthEvents = appHealth.HealthEvents.Where(
                                s => s.HealthInformation.HealthState == HealthState.Warning
                                || s.HealthInformation.HealthState == HealthState.Error);

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

                // Since FH can run on each node (-1 InstanceCount), if this is the case then have FH only try to repair app services that are also running on the same node.
                // This removes the need to try and orchestrate repairs across nodes (which we will have to do in the non -1 case).
                if (_instanceCount == -1 && repairData.NodeName != serviceContext.NodeContext.NodeName)
                {
                    continue;
                }
                else if (_instanceCount > 1)
                {
                    // Randomly wait to decrease chances of simultaneous ownership among FH instances.
                    await RandomWaitAsync();
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

                if (appName.OriginalString == RepairConstants.SystemAppName)
                {
                    if (!ConfigSettings.EnableSystemAppRepair)
                    {
                        continue;
                    }

                    // Block attempts to schedule node-level or system service restart repairs if one is already executing in the cluster.
                    var fhRepairTasks = await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token);
                    
                    if (fhRepairTasks.Count > 0)
                    {
                        foreach (var repair in fhRepairTasks)
                        {
                            var executorData = JsonSerializationUtility.TryDeserializeObject(repair.ExecutorData, out RepairExecutorData exData) ? exData : null;

                            if (executorData?.RepairData?.RepairPolicy?.RepairAction != RepairActionType.RestartFabricNode &&
                                executorData?.RepairData?.RepairPolicy?.RepairAction != RepairActionType.RestartProcess)
                            {
                                continue;
                            }

                            string message = $"A Service Fabric System service repair ({repair.TaskId}) is already in progress in the cluster(state: {repair.State}). " +
                                             $"Will not attempt repair at this time.";

                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    $"ProcessApplicationHealth::System::{repair.TaskId}",
                                    message,
                                    Token,
                                    null,
                                    ConfigSettings.EnableVerboseLogging);

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

                    var currentRepairs =
                        await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token);

                    // Is a repair for the target app service instance already happening in the cluster?
                    // There can be multiple Warnings emitted by FO for a single app at the same time.
                    if (currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(repairData.ProcessName)))
                    {
                        var repair = currentRepairs.FirstOrDefault(r => r.ExecutorData.Contains(repairData.ProcessName));
                        await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                $"MonitorRepairableHealthEventsAsync::{repairData.ProcessName}",
                                $"There is already a repair in progress for Fabric system service {repairData.ProcessName}(state: {repair.State})",
                                Token,
                                null,
                                ConfigSettings.EnableVerboseLogging);

                        continue;
                    }

                    // Repair already in progress?
                    if (currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(repairId)))
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

                    // Don't restart thyself.
                    if (repairData.ServiceName == serviceContext.ServiceName.OriginalString && repairData.NodeName == serviceContext.NodeContext.NodeName)
                    {
                        continue;
                    }

                    repairRules = GetRepairRulesForTelemetryData(repairData);

                    // Nothing to do here.
                    if (repairRules == null || repairRules?.Count == 0)
                    {
                        continue;
                    }

                    string serviceProcessName = $"{repairData.ServiceName?.Replace("fabric:/", "").Replace("/", "")}";
                    var currentRepairs =
                        await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token);

                    // This is the way each FH repair is ID'd. This data is stored in the related Repair Task's ExecutorData property.
                    repairId = $"{repairData.NodeName}_{serviceProcessName}_{repairData.Metric?.Replace(" ", string.Empty)}";

                    // Is a repair for the target app service instance already happening in the cluster?
                    // There can be multiple Warnings emitted by FO for a single app at the same time.
                    if (currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(repairId)))
                    {
                        var repair = currentRepairs.FirstOrDefault(r => r.ExecutorData.Contains(repairId));
                        await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                $"MonitorRepairableHealthEventsAsync::{repairData.ServiceName}",
                                $"{appName} already has a repair in progress for service {repairData.ServiceName}(state: {repair.State})",
                                Token,
                                null,
                                ConfigSettings.EnableVerboseLogging);

                        continue;
                    }
                }

                /* Start repair workflow */

                repairData.RepairPolicy = new RepairPolicy
                {
                    RepairId = repairId
                };
                repairData.Property = evt.HealthInformation.Property;
                string errOrWarn = "Error";

                if (evt.HealthInformation.HealthState == HealthState.Warning)
                {
                    errOrWarn = "Warning";
                }

                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"MonitorRepairableHealthEventsAsync:{repairId}",
                        $"Detected {errOrWarn} state for Application {repairData.ApplicationName}{Environment.NewLine}" +
                        $"SourceId: {evt.HealthInformation.SourceId}{Environment.NewLine}" +
                        $"Property: {evt.HealthInformation.Property}{Environment.NewLine}" +
                        $"{system}Application repair policy is enabled. " +
                        $"{repairRules.Count} Logic rules found for {system}Application-level repair.",
                        Token,
                        null,
                        ConfigSettings.EnableVerboseLogging);

                // Update the in-memory HealthEvent List.
                this.repairTaskManager.DetectedHealthEvents.Add(evt);

                // Start the repair workflow.
                await repairTaskManager.StartRepairWorkflowAsync(repairData, repairRules, Token);
            }
        }

        private async Task ProcessServiceHealthAsync(ServiceHealthState serviceHealthState)
        {
            // This is just used to make sure there is more than 1 node in the cluster. We don't need a list of all nodes.
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

            ServiceHealth serviceHealth;
            Uri appName;
            Uri serviceName = serviceHealthState.ServiceName;

            // User service target? Do not proceed if App repair is not enabled.
            if (!serviceName.OriginalString.Contains(RepairConstants.SystemAppName) && !ConfigSettings.EnableAppRepair)
            {
                return;
            }

            // System service target? Do not proceed if system app repair is not enabled.
            if (serviceName.OriginalString.Contains(RepairConstants.SystemAppName) && !ConfigSettings.EnableSystemAppRepair)
            {
                return;
            }

            serviceHealth = await FabricClientSingleton.HealthManager.GetServiceHealthAsync(serviceName, ConfigSettings.AsyncTimeout, Token);
            var name = await FabricClientSingleton.QueryManager.GetApplicationNameAsync(serviceName, ConfigSettings.AsyncTimeout, Token);
            appName = name.ApplicationName;

            // user service - upgrade check.
            if (!appName.OriginalString.Contains(RepairConstants.SystemAppName) && !serviceName.OriginalString.Contains(RepairConstants.SystemAppName))
            {
                try
                {
                    var app = await FabricClientSingleton.QueryManager.GetApplicationNameAsync(serviceName, ConfigSettings.AsyncTimeout, Token);
                    var appUpgradeStatus = await FabricClientSingleton.ApplicationManager.GetApplicationUpgradeProgressAsync(app.ApplicationName);

                    if (appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingBackInProgress
                        || appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingForwardInProgress
                        || appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingForwardPending)
                    {
                        List<int> udInAppUpgrade = await UpgradeChecker.GetUdsWhereApplicationUpgradeInProgressAsync(serviceName, Token);
                        string udText = string.Empty;

                        // -1 means no upgrade in progress for application.
                        if (udInAppUpgrade.Any(ud => ud > -1))
                        {
                            udText = $"in UD {udInAppUpgrade.First(ud => ud > -1)}";
                        }

                        string telemetryDescription = $"{app.ApplicationName} is upgrading {udText}. Will not attempt service repair at this time.";

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
                catch (FabricException)
                {
                    // This upgrade check should not prevent moving forward if the fabric client call fails with an FE.
                }
            }

            var healthEvents = serviceHealth.HealthEvents.Where(
                e => e.HealthInformation.HealthState == HealthState.Warning || e.HealthInformation.HealthState == HealthState.Error);

            // Replica repair. This only makes sense if a partition is in Error or Warning state (and Replica repair is still experimental for FH).
            if (ConfigSettings.EnableReplicaRepair)
            {
                var partitionHealthStates = serviceHealth.PartitionHealthStates.Where(
                    p => p.AggregatedHealthState == HealthState.Warning || p.AggregatedHealthState == HealthState.Error);

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

                // PartitionId is Guid? in ITelemetryData.
                if (repairData.PartitionId == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(repairData.ApplicationName))
                {
                    repairData.ApplicationName = appName.OriginalString;
                }

                // Since FH can run on each node (-1 InstanceCount), if this is the case then have FH only try to repair app services that are also running on the same node.
                // This removes the need to try and orchestrate repairs across nodes (which we will have to do in the non -1 case).
                if (_instanceCount == -1 && repairData.NodeName != serviceContext.NodeContext.NodeName)
                {
                    continue;
                }
                else if (_instanceCount > 1)
                {
                    // Randomly wait to decrease chances of simultaneous ownership among FH instances.
                    await RandomWaitAsync();
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

                if (serviceName.OriginalString.Contains(RepairConstants.SystemAppName))
                {
                    if (!ConfigSettings.EnableSystemAppRepair)
                    {
                        continue;
                    }

                    // Block attempts to schedule node-level or system service restart repairs if one is already executing in the cluster.
                    var fhRepairTasks = await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(
                                                RepairTaskEngine.FabricHealerExecutorName,
                                                Token);

                    if (fhRepairTasks.Count > 0)
                    {
                        foreach (var repair in fhRepairTasks)
                        {
                            var executorData = JsonSerializationUtility.TryDeserializeObject(repair.ExecutorData, out RepairExecutorData exData) ? exData : null;

                            if (executorData?.RepairData?.RepairPolicy?.RepairAction != RepairActionType.RestartFabricNode &&
                                executorData?.RepairData?.RepairPolicy?.RepairAction != RepairActionType.RestartProcess)
                            {
                                continue;
                            }

                            string message = $"A Service Fabric System service repair ({repair.TaskId}) is already in progress in the cluster(state: {repair.State}). " +
                                             $"Will not attempt repair at this time.";

                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    $"ProcessApplicationHealth::System::{repair.TaskId}",
                                    message,
                                    Token,
                                    null,
                                    ConfigSettings.EnableVerboseLogging);

                            return;
                        }
                    }

                    repairRules = GetRepairRulesForTelemetryData(repairData);

                    if (repairRules == null || repairRules?.Count == 0)
                    {
                        continue;
                    }

                    repairId = $"{repairData.NodeName}_{serviceName.OriginalString}_{repairData.Code}";
                    system = "System ";

                    var currentRepairs =
                        await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token);

                    // Is a repair for the target app service instance already happening in the cluster?
                    // There can be multiple Warnings emitted by FO for a single app at the same time.
                    if (currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(serviceName.OriginalString)))
                    {
                        var repair = currentRepairs.FirstOrDefault(r => r.ExecutorData.Contains(serviceName.OriginalString));
                        await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                $"MonitorRepairableHealthEventsAsync::{serviceName.OriginalString}",
                                $"There is already a repair in progress for Fabric system service {serviceName.OriginalString}(state: {repair.State})",
                                Token,
                                null,
                                ConfigSettings.EnableVerboseLogging);

                        continue;
                    }

                    // Repair already in progress?
                    if (currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(repairId)))
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

                    // Don't restart thyself.
                    if (repairData.ServiceName == serviceContext.ServiceName.OriginalString && repairData.NodeName == serviceContext.NodeContext.NodeName)
                    {
                        continue;
                    }

                    repairRules = GetRepairRulesForTelemetryData(repairData);

                    // Nothing to do here.
                    if (repairRules == null || repairRules?.Count == 0)
                    {
                        continue;
                    }

                    string serviceProcessName = $"{repairData.ServiceName.Replace("fabric:/", "").Replace("/", "")}";
                    var currentFHRepairs =
                        await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token);

                    // This is the way each FH repair is ID'd. This data is stored in the related Repair Task's ExecutorData property.
                    repairId = $"{repairData.NodeName}_{serviceProcessName}_{repairData.Metric?.Replace(" ", string.Empty)}";

                    // All FH repairs have serialized instances of RepairExecutorData set as the value for a RepairTask's ExecutorData property.
                    if (currentFHRepairs?.Count > 0)
                    {
                        // This prevents starting creating a new repair if another service running on a different node needs to be resarted, for example.
                        // Thing of this as a UD Walk across nodes of service instances in need of repair.
                        if (ConfigSettings.EnableRollingServiceRestarts
                            && nodes?.Count > 1
                            && currentFHRepairs.Any(r => JsonSerializationUtility.TryDeserializeObject(r.ExecutorData, out RepairExecutorData execData)
                                                      && execData?.RepairData?.ServiceName?.ToLower() == repairData.ServiceName.ToLower()))
                        {
                            var repair = currentFHRepairs.FirstOrDefault(r => JsonSerializationUtility.TryDeserializeObject(r.ExecutorData, out RepairExecutorData execData)
                                                                           && execData.RepairData.ServiceName.ToLower() == repairData.ServiceName.ToLower());

                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    $"{serviceProcessName}_RollingRepairInProgress",
                                    $"There is currently a rolling repair in progress for service {repairData.ServiceName}. Current node: {repair.Target}. Repair State: {repair.State})",
                                    Token,
                                    null,
                                    ConfigSettings.EnableVerboseLogging);

                            return;
                        }
                        // For the case where a service repair is still not Completed (e.g., the repair status is Restoring, which would happen after the repair executor has completed
                        // its work, but RM is performing post safety checks (safety checks can be enabled/disabled in logic rules).
                        else if (currentFHRepairs.Any(r => JsonSerializationUtility.TryDeserializeObject(r.ExecutorData, out RepairExecutorData execData)
                                                        && execData?.RepairData?.RepairPolicy?.RepairId == repairId))
                        {
                            var repair = currentFHRepairs.FirstOrDefault(r => JsonSerializationUtility.TryDeserializeObject(r.ExecutorData, out RepairExecutorData execData)
                                                                           && execData.RepairData.RepairPolicy.RepairId == repairId);

                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    $"{serviceProcessName}_RepairAlreadyInProgress",
                                    $"There is currently a repair in progress for service {repairData.ServiceName} on node {repairData.NodeName}. Repair State: {repair.State}.",
                                    Token,
                                    null,
                                    ConfigSettings.EnableVerboseLogging);

                            return;
                        }
                        // This means no other service instance will get repaired anywhere in the cluster if a service repair job for the same service is
                        // already taking place on any node. This only takes effect if config setting EnableRollingServiceRestarts is set to true.
                    }
                }

                /* Start repair workflow */
                repairData.RepairPolicy = new RepairPolicy
                {
                    RepairId = repairId
                };
                repairData.Property = evt.HealthInformation.Property;
                string errOrWarn = "Error";

                if (evt.HealthInformation.HealthState == HealthState.Warning)
                {
                    errOrWarn = "Warning";
                }

                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"MonitorRepairableHealthEventsAsync:{repairId}",
                        $"Detected {errOrWarn} state for Service {repairData.ServiceName}{Environment.NewLine}" +
                        $"SourceId: {evt.HealthInformation.SourceId}{Environment.NewLine}" +
                        $"Property: {evt.HealthInformation.Property}{Environment.NewLine}" +
                        $"{system}Application repair policy is enabled. " +
                        $"{repairRules.Count} Logic rules found for {system}Service-level repair.",
                        Token,
                        null,
                        ConfigSettings.EnableVerboseLogging);

                // Update the in-memory HealthEvent List.
                repairTaskManager.DetectedHealthEvents.Add(evt);

                // Start the repair workflow.
                await repairTaskManager.StartRepairWorkflowAsync(repairData, repairRules, Token);
            }
        }

        private async Task ProcessNodeHealthAsync(IEnumerable<NodeHealthState> nodeHealthStates)
        {
            // This is just used to make sure there is more than 1 node in the cluster. We don't need a list of all nodes.
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

            // Node/Machine repair not supported in onebox clusters..
            if (nodes?.Count == 1)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"ProcessMachineHealth::NotSupported",
                        "Machine/Fabric Node repair is not supported in clusters with 1 Fabric node.",
                        Token,
                        null,
                        ConfigSettings.EnableVerboseLogging);

               return;
            }

            var supportedNodeHealthStates =
                nodeHealthStates.Where(a => a.AggregatedHealthState == HealthState.Warning || a.AggregatedHealthState == HealthState.Error);

            foreach (var node in supportedNodeHealthStates)
            {
                Token.ThrowIfCancellationRequested();

                var nodeList = await FabricClientSingleton.QueryManager.GetNodeListAsync(node.NodeName, ConfigSettings.AsyncTimeout, Token);
                string nodeType = nodeList[0].NodeType;
                string nodeUD = nodeList[0].UpgradeDomain;
                var nodeHealth = await FabricClientSingleton.HealthManager.GetNodeHealthAsync(node.NodeName, ConfigSettings.AsyncTimeout, Token);
                var nodeHealthEvents =
                    nodeHealth.HealthEvents.Where(
                                s => (s.HealthInformation.HealthState == HealthState.Warning || s.HealthInformation.HealthState == HealthState.Error));

                // Ensure a node in Error is not in error due to being down as part of a cluster upgrade or infra update in its UD.
                if (node.AggregatedHealthState == HealthState.Error)
                {
                    string udInClusterUpgrade = await UpgradeChecker.GetCurrentUDWhereFabricUpgradeInProgressAsync(Token);

                    if (!string.IsNullOrWhiteSpace(udInClusterUpgrade) && udInClusterUpgrade == nodeUD)
                    {
                        string telemetryDescription =
                            $"Cluster is currently upgrading in UD \"{udInClusterUpgrade}\", which is the UD for node {node.NodeName}, which is down. " +
                            "Will not schedule or execute node-level repair at this time.";

                        await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                $"{node.NodeName}_down_ClusterUpgrade({nodeUD})",
                                telemetryDescription,
                                Token,
                                null,
                                ConfigSettings.EnableVerboseLogging);

                        continue;
                    }

                    // Check to see if an Azure tenant/platform update is in progress for target node. Do not conduct repairs if so.
                    if (await UpgradeChecker.IsAzureUpdateInProgress(nodeType, node.NodeName, Token))
                    {
                        string telemetryDescription = $"{node.NodeName} is down due to Infra repair job (UD = {nodeUD}). Will not attempt node repair at this time.";

                        await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                $"{node.NodeName}_down_InfraRepair",
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
                    await RandomWaitAsync();

                    // Was health event generated by FO or FHProxy?
                    if (!JsonSerializationUtility.TryDeserializeObject(evt.HealthInformation.Description, out TelemetryData repairData))
                    {
                        // This will enable Machine level repair (reboot, reimage) based on detected SF Node Health Event not generated by FO/FHProxy.
                        repairData = new TelemetryData
                        {
                            NodeName = node.NodeName,
                            NodeType = nodeType,
                            EntityType = EntityType.Machine,
                            Description = evt.HealthInformation.Description,
                            HealthState = evt.HealthInformation.HealthState,
                            Source = RepairConstants.FabricHealer
                        };
                    }
                    else
                    {
                        // Disk?
                        if (repairData.EntityType == EntityType.Disk && ConfigSettings.EnableDiskRepair)
                        {
                            await ProcessDiskHealthAsync(evt, repairData);
                            continue;
                        }

                        // Fabric node?
                        if (repairData.EntityType == EntityType.Node && ConfigSettings.EnableFabricNodeRepair)
                        {
                            // FabricHealerProxy-generated report, so a restart fabric node request, for example.
                            await ProcessFabricNodeHealthAsync(evt, repairData);
                            continue;
                        }
                    }

                    // Machine repair \\

                    if (!ConfigSettings.EnableMachineRepair)
                    {
                        continue;
                    }

                    // If there are mulitple instances of FH deployed to the cluster (like -1 InstanceCount), then don't do machine repairs if this instance of FH 
                    // detects a need to do so. Another instance on a different node will take the job. Only DiskObserver-generated repair data has to be done on the node
                    // where FO's DiskObserver emitted the related information, for example (like Disk space issues and the need to clean specified (in logic rules) folders).
                    if ((_instanceCount == -1 || _instanceCount > 2) && node.NodeName == serviceContext.NodeContext.NodeName)
                    {
                        continue;
                    }

                    // Make sure that there is not already an Infra repair in progress for the target node.
                    var currentISRepairs =
                        await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync($"{RepairConstants.InfrastructureServiceName}/{nodeType}", Token);

                    if (currentISRepairs?.Count > 0)
                    {
                        if (currentISRepairs.Any(r => r.Description.Contains(node.NodeName)))
                        {
                            var repair = currentISRepairs.FirstOrDefault(r => r.Description.Contains(node.NodeName));

                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                    LogLevel.Info,
                                    $"{node.NodeName}_MachineRepairAlreadyInProgress",
                                    $"There is currently a Machine repair in progress for node {node.NodeName}. Repair State: {repair.State}.",
                                    Token,
                                    null,
                                    ConfigSettings.EnableVerboseLogging);

                            return;
                        }
                    }

                    // Get repair rules for supplied facts (TelemetryData).
                    var repairRules = GetRepairRulesForTelemetryData(repairData);

                    if (repairRules == null || repairRules.Count == 0)
                    {
                        continue;
                    }

                    /* Start repair workflow */

                    string repairId = $"Machine_Repair_{nodeType}_{repairData.NodeName}";
                    repairData.RepairPolicy = new RepairPolicy
                    {
                        RepairId = repairId
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

                    // Update the in-memory HealthEvent List.
                    repairTaskManager.DetectedHealthEvents.Add(evt);

                    // Start the repair workflow.
                    await repairTaskManager.StartRepairWorkflowAsync(repairData, repairRules, Token);
                }
            }
        }

        private async Task ProcessDiskHealthAsync(HealthEvent evt, TelemetryData repairData)
        {
            // Can only repair local disks.
            if (repairData.NodeName != serviceContext.NodeContext.NodeName)
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
                RepairId = repairId
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

            // Update the in-memory HealthEvent List.
            repairTaskManager.DetectedHealthEvents.Add(evt);

            // Start the repair workflow.
            await repairTaskManager.StartRepairWorkflowAsync(repairData, repairRules, Token);
        }

        private async Task ProcessFabricNodeHealthAsync(HealthEvent healthEvent, TelemetryData repairData)
        {
            var repairRules = GetRepairRulesForTelemetryData(repairData);

            if (repairRules == null || repairRules?.Count == 0)
            {
                return;
            }

            // There is only one supported repair for a FabricNode: Restart.
            string repairId = $"{repairData.NodeName}_{repairData.NodeType}_Restart";

            var currentRepairs =
                await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token);
            
            // Block attempts to reschedule another Fabric node-level repair for the same node if a current repair has not yet completed.
            if (currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(repairId)))
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
                RepairId = repairId
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

            // Update the in-memory HealthEvent List.
            repairTaskManager.DetectedHealthEvents.Add(healthEvent);

            // Start the repair workflow.
            await repairTaskManager.StartRepairWorkflowAsync(repairData, repairRules, Token);
        }

        // This is an example of a repair for a non-FO-originating health event. This function needs some work, but you get the basic idea here.
        // FO does not support replica monitoring and as such it does not emit specific error codes that FH recognizes.
        // *This is an experimental function/workflow in need of more testing.*
        private async Task ProcessReplicaHealthAsync(ServiceHealth serviceHealth)
        {
            // Random wait to limit potential duplicate (concurrent) repair job creation from other FH instances.
            await RandomWaitAsync();

            /*  Example of repairable problem at Replica level, as health event:

                [SourceId] ='System.RAP' reported Warning/Error for property...
                [Property] = 'IStatefulServiceReplica.ChangeRole(N)Duration'.
                [Description] = The api IStatefulServiceReplica.ChangeRole(N) on node [NodeName] is stuck. 

                Start Time (UTC): 2020-04-26 19:22:55.492. 
            */

            List<HealthEvent> healthEvents = new List<HealthEvent>();
            var partitionHealthStates = serviceHealth.PartitionHealthStates.Where(
                p => p.AggregatedHealthState == HealthState.Warning || p.AggregatedHealthState == HealthState.Error);

            foreach (var partitionHealthState in partitionHealthStates)
            {
                PartitionHealth partitionHealth = 
                    await FabricClientSingleton.HealthManager.GetPartitionHealthAsync(partitionHealthState.PartitionId, ConfigSettings.AsyncTimeout, Token);
                
                List<ReplicaHealthState> replicaHealthStates = partitionHealth.ReplicaHealthStates.Where(
                    p => p.AggregatedHealthState == HealthState.Warning || p.AggregatedHealthState == HealthState.Error).ToList();

                if (replicaHealthStates != null && replicaHealthStates.Count > 0)
                {
                    foreach (var rep in replicaHealthStates)
                    {
                        var replicaHealth =
                            await FabricClientSingleton.HealthManager.GetReplicaHealthAsync(partitionHealthState.PartitionId, rep.Id, ConfigSettings.AsyncTimeout, Token);

                        if (replicaHealth != null)
                        {
                            healthEvents = replicaHealth.HealthEvents.Where(
                                h => h.HealthInformation.HealthState == HealthState.Warning || h.HealthInformation.HealthState == HealthState.Error).ToList();
                            
                            foreach (HealthEvent healthEvent in healthEvents)
                            {
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
                                    PartitionId = rep.PartitionId,
                                    ServiceName = serviceHealth.ServiceName.OriginalString,
                                    Source = RepairConstants.FabricHealerAppName
                                };

                                string errOrWarn = "Error";

                                if (healthEvent.HealthInformation.HealthState == HealthState.Warning)
                                {
                                    errOrWarn = "Warning";
                                }

                                string repairId = $"{nodeName}_{serviceHealth.ServiceName.OriginalString.Remove(0, appName.Length + 1)}_{repairData.PartitionId}";
                                repairData.RepairPolicy = new RepairPolicy
                                {
                                    RepairId = repairId
                                };
                                repairData.Property = healthEvent.HealthInformation.Property;

                                // Repair already in progress?
                                var currentRepairs = await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token);

                                if (currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(repairId)))
                                {
                                    continue;
                                }

                                /* Start repair workflow */

                                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        $"MonitorRepairableHealthEventsAsync:Replica_{rep.Id}_{errOrWarn}",
                                        $"Detected Replica {rep.Id} on Partition " +
                                        $"{rep.PartitionId} is in {errOrWarn}.{Environment.NewLine}" +
                                        $"Replica repair policy is enabled. " +
                                        $"{repairRules.Count} Logic rules found for Replica repair.",
                                        Token,
                                        null,
                                        ConfigSettings.EnableVerboseLogging);

                                // Start the repair workflow.
                                await repairTaskManager.StartRepairWorkflowAsync(repairData, repairRules, Token);
                            }
                        }
                    }
                }
            }
        }
        
        private List<string> GetRepairRulesFromErrorCode(string errorCode, string app = null)
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
                case SupportedErrorCodes.AppErrorTooManyOpenFileHandles:
                case SupportedErrorCodes.AppErrorTooManyThreads:
                case SupportedErrorCodes.AppWarningCpuPercent:
                case SupportedErrorCodes.AppWarningMemoryMB:
                case SupportedErrorCodes.AppWarningMemoryPercent:
                case SupportedErrorCodes.AppWarningTooManyActiveEphemeralPorts:
                case SupportedErrorCodes.AppWarningTooManyActiveTcpPorts:
                case SupportedErrorCodes.AppWarningTooManyOpenFileHandles:
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
                case SupportedErrorCodes.NodeErrorTotalOpenFileHandlesPercent:
                case SupportedErrorCodes.NodeWarningCpuPercent:
                case SupportedErrorCodes.NodeWarningMemoryMB:
                case SupportedErrorCodes.NodeWarningMemoryPercent:
                case SupportedErrorCodes.NodeWarningTooManyActiveEphemeralPorts:
                case SupportedErrorCodes.NodeWarningTooManyActiveTcpPorts:
                case SupportedErrorCodes.NodeWarningTotalOpenFileHandlesPercent:

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

        private List<string> GetRepairRulesForSupportedObserver(string observerName)
        {
            string repairPolicySectionName;

            switch (observerName)
            {
                // App repair (user).
                case RepairConstants.AppObserver:

                    repairPolicySectionName =  RepairConstants.AppRepairPolicySectionName;
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

        /// <summary>
        /// Get a list of rules that correspond to the supplied EntityType.
        /// </summary>
        /// <param name="repairData">Instance of TelemetryData that FabricHealer deserialized from a Health Event Description.</param>
        /// <returns></returns>
        private List<string> GetRepairRulesForTelemetryData(TelemetryData repairData)
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
                case EntityType.Disk when serviceContext.NodeContext.NodeName == repairData.NodeName:
                    repairPolicySectionName = RepairConstants.DiskRepairPolicySectionName;
                    break;

                // Machine repair.
                case EntityType.Machine:
                    repairPolicySectionName = RepairConstants.MachineRepairPolicySectionName;
                    break;

                // Fabric Node repair (from FabricHealerProxy, for example, where there is no concept of Observer).
                case EntityType.Node:
                    repairPolicySectionName = RepairConstants.FabricNodeRepairPolicySectionName;
                    break;

                default:
                    return null;
            }

            return GetRepairRulesFromConfiguration(repairPolicySectionName);
        }

        private List<string> GetRepairRulesFromConfiguration(string repairPolicySectionName)
        {
            try
            {
                string logicRulesConfigFileName =
                    GetSettingParameterValue(serviceContext, repairPolicySectionName, RepairConstants.LogicRulesConfigurationFile);
                var configPath = serviceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Path;
                var rulesFolderPath = Path.Combine(configPath, RepairConstants.LogicRulesFolderName);
                var rulesFilePath = Path.Combine(rulesFolderPath, logicRulesConfigFileName);

                if (!File.Exists(rulesFilePath))
                {
                    return null;
                }

                string[] rules = File.ReadAllLines(rulesFilePath);

                if (rules.Length == 0)
                {
                    return null;
                }

                List<string> repairRules = ParseRulesFile(rules);
                return repairRules;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException)
            {
                return null;
            }
        }

        private int GetEnabledRepairRuleCount()
        {
            var config = serviceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config");
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
                _fabricClient?.Dispose();
            }

            disposedValue = true;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        private static List<string> ParseRulesFile(string[] rules)
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

        private async Task RandomWaitAsync()
        {
            var random = new Random();
            int waitTimeMS = random.Next(random.Next(100, nodeCount * 100), 1000 * nodeCount);

            await Task.Delay(waitTimeMS, Token);
        }

        // https://stackoverflow.com/questions/25678690/how-can-i-check-github-releases-in-c
        private async Task CheckGithubForNewVersionAsync()
        {
            try
            {
                var githubClient = new GitHubClient(new ProductHeaderValue(RepairConstants.FabricHealer));
                IReadOnlyList<Release> releases = await githubClient.Repository.Release.GetAll("microsoft", "service-fabric-healer");

                if (releases.Count == 0)
                {
                    return;
                }

                string releaseAssetName = releases[0].Name;
                string latestVersion = releaseAssetName.Split(" ")[1];
                Version latestGitHubVersion = new Version(latestVersion);
                Version localVersion = new Version(InternalVersionNumber);
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
            }
            catch (Exception e)
            {
                // Don't take down FO due to error in version check.
                RepairLogger.LogWarning($"Failure in CheckGithubForNewVersionAsync:{Environment.NewLine}{e}");
            }
        }

        private async Task ClearExistingHealthReportsAsync()
        {
            try
            {
                var healthReporter = new FabricHealthReporter(RepairLogger);
                var healthReport = new HealthReport
                {
                    HealthMessage = "Clearing existing health reports as FabricHealer is stopping or updating.",
                    NodeName = serviceContext.NodeContext.NodeName,
                    State = HealthState.Ok,
                    HealthReportTimeToLive = TimeSpan.FromMinutes(5),
                };

                var appName = new Uri(RepairConstants.FabricHealerAppName);
                var appHealth = await FabricClientSingleton.HealthManager.GetApplicationHealthAsync(appName);
                var FHAppEvents = appHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains($"{RepairConstants.FabricHealer}."));

                foreach (HealthEvent evt in FHAppEvents)
                {
                    healthReport.AppName = appName;
                    healthReport.Property = evt.HealthInformation.Property;
                    healthReport.SourceId = evt.HealthInformation.SourceId;
                    healthReport.EntityType = EntityType.Application;

                    healthReporter.ReportHealthToServiceFabric(healthReport);
                    Thread.Sleep(50);
                }

                var nodeHealth = await FabricClientSingleton.HealthManager.GetNodeHealthAsync(serviceContext.NodeContext.NodeName);
                var FHNodeEvents = nodeHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(RepairConstants.FabricHealer));

                foreach (HealthEvent evt in FHNodeEvents)
                {
                    healthReport.Property = evt.HealthInformation.Property;
                    healthReport.SourceId = evt.HealthInformation.SourceId;
                    healthReport.EntityType = EntityType.Node;

                    healthReporter.ReportHealthToServiceFabric(healthReport);
                    Thread.Sleep(50);
                }
            }
            catch (FabricException)
            {

            }
        }

        private string GetServiceFabricRuntimeVersion()
        {
            try
            {
                var config = ServiceFabricConfiguration.Instance;
                return config.FabricVersion;
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                RepairLogger.LogWarning($"GetServiceFabricRuntimeVersion failure:{Environment.NewLine}{e}");
            }

            return null;
        }
    }
}
