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

namespace FabricHealer
{
    public sealed class FabricHealerManager : IDisposable
    {
        internal static TelemetryUtilities TelemetryUtilities;
        internal static RepairData RepairHistory;

        // Folks often use their own version numbers. This is for internal diagnostic telemetry.
        private const string InternalVersionNumber = "1.1.0";
        private static FabricHealerManager singleton;
        private bool disposedValue;
        private readonly StatelessServiceContext serviceContext;
        private readonly FabricClient fabricClient;
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

        internal static Logger RepairLogger
        {
            get;
            private set;
        }

        private bool FabricHealerOperationalTelemetryEnabled
        {
            get; set;
        }

        // CancellationToken from FabricHealer.RunAsync.
        private CancellationToken Token
        {
            get;
        }

        private DateTime LastTelemetrySendDate
        {
            get; set;
        }

        private DateTime LastVersionCheckDateTime
        {
            get; set;
        }

        private bool EtwEnabled 
        { 
            get; set; 
        }

        public static ConfigSettings ConfigSettings
        {
            get; set;
        }

        private FabricHealerManager(StatelessServiceContext context, CancellationToken token)
        {
            serviceContext = context;
            fabricClient = new FabricClient(FabricClientRole.Admin);
            Token = token;
            serviceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent += CodePackageActivationContext_ConfigurationPackageModifiedEvent;
            ConfigSettings = new ConfigSettings(context);
            TelemetryUtilities = new TelemetryUtilities(fabricClient, context);
            repairTaskEngine = new RepairTaskEngine(fabricClient);
            repairTaskManager = new RepairTaskManager(fabricClient, serviceContext, Token);
            RepairLogger = new Logger(RepairConstants.FabricHealer, ConfigSettings.LocalLogPathParameter)
            {
                EnableVerboseLogging = ConfigSettings.EnableVerboseLogging
            };

            RepairHistory = new RepairData();
            healthReporter = new FabricHealthReporter(fabricClient);
            sfRuntimeVersion = GetServiceFabricRuntimeVersion();
        }

        /// <summary>
        /// This is the static singleton instance of FabricHealerManager type. FabricHealerManager does not support
        /// multiple instantiations. It does not provide a public constructor.
        /// </summary>
        /// <param name="context">StatefulService context instance.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The singleton instance of FabricHealerManager.</returns>
        public static FabricHealerManager Instance(StatelessServiceContext context, CancellationToken token)
        {
            return singleton ??= new FabricHealerManager(context ?? throw new ArgumentException("context can't be null"), token);
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
                ReportType = HealthReportType.Application,
                HealthMessage = okMessage,
                State = HealthState.Ok,
                Property = "RequirementCheck::RMDeployed",
                HealthReportTimeToLive = TimeSpan.FromMinutes(5),
                SourceId = RepairConstants.FabricHealer,
            };
            ServiceList serviceList = await fabricClient.QueryManager.GetServiceListAsync(
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
                await fabricClient.ServiceManager.GetServiceDescriptionAsync(serviceContext.ServiceName, ConfigSettings.AsyncTimeout, Token);

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
                                                  () => fabricClient.QueryManager.GetNodeListAsync(null, ConfigSettings.AsyncTimeout, Token),
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
                            ConfigSettings.ExecutionLoopSleepSeconds > 0 ? ConfigSettings.ExecutionLoopSleepSeconds : 10), Token);      
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
            FabricHealerOperationalEventData telemetryData = null;

            try
            {
                RepairHistory.EnabledRepairCount = GetEnabledRepairRuleCount();

                telemetryData = new FabricHealerOperationalEventData
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

            return telemetryData;
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
                                                           Token), Token);

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

                    if (!JsonSerializationUtility.TryDeserialize(executorData, out RepairExecutorData repairExecutorData))
                    {
                        continue;
                    }

                    // Don't do anything if the orphaned repair was for a different node than this one:
                    // FH runs on all nodes, so don't cancel repairs for any other node than your own.
                    if (repairExecutorData.NodeName != serviceContext.NodeContext.NodeName)
                    {
                        continue;
                    }

                    // Try and cancel existing repair. We may need to create a new one for abandoned repairs where FH goes down for some reason.
                    // Note: CancelRepairTaskAsync handles exceptions (IOE) that may be thrown by RM due to state change policy. 
                    // The repair state could change to Completed after this call is made, for example, and before RM API call.
                    if (repair.State != RepairTaskState.Completed)
                    {
                        await FabricRepairTasks.CancelRepairTaskAsync(repair, fabricClient);
                    }

                    /* Resume interrupted Fabric Node restart repairs */

                    // There is no need to resume simple repairs that do not require multiple repair steps (e.g., codepackage/process/replica restarts).
                    if (repairExecutorData.RepairPolicy.RepairAction != RepairActionType.RestartFabricNode)
                    {
                        continue;
                    }

                    string errorCode = repairExecutorData.ErrorCode;
                    
                    if (string.IsNullOrWhiteSpace(errorCode))
                    {
                        continue;
                    }

                    // File Deletion repair is a node-level (VM) repair, but is not multi-step. Ignore.
                    if (SupportedErrorCodes.GetCodeNameFromErrorCode(errorCode).Contains("Disk"))
                    {
                        continue;
                    }

                    // Fabric System service warnings/errors from FO can be Node level repair targets (e.g., Fabric binary needs to be restarted).
                    // FH will restart the node hosting the troubled SF system service if specified in related logic rules.
                    var repairRules = GetRepairRulesFromConfiguration(
                        !string.IsNullOrWhiteSpace(repairExecutorData.SystemServiceProcessName) ? RepairConstants.SystemAppRepairPolicySectionName : RepairConstants.FabricNodeRepairPolicySectionName);

                    var repairData = new TelemetryData
                    {
                        NodeName = repairExecutorData.NodeName,
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
                var clusterHealth = await fabricClient.HealthManager.GetClusterHealthAsync(ConfigSettings.AsyncTimeout, Token);

                if (clusterHealth.AggregatedHealthState == HealthState.Ok)
                {
                    return;
                }

                // Check cluster upgrade status. If the cluster is upgrading to a new version (or rolling back)
                // then do not attempt repairs.
                try
                {
                    int udInClusterUpgrade = await UpgradeChecker.GetUdsWhereFabricUpgradeInProgressAsync(fabricClient, Token);

                    if (udInClusterUpgrade > -1)
                    {
                        string telemetryDescription = $"Cluster is currently upgrading in UD {udInClusterUpgrade}. Will not schedule or execute repairs at this time.";
                        await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                     LogLevel.Info,
                                                     "MonitorRepairableHealthEventsAsync::ClusterUpgradeDetected",
                                                     telemetryDescription,
                                                     Token,
                                                     null,
                                                     ConfigSettings.EnableVerboseLogging);
                        return;
                    }

                    // Check to see if an Azure tenant update is in progress. Do not conduct repairs if so.
                    if (await UpgradeChecker.IsAzureTenantUpdateInProgress(fabricClient, serviceContext.NodeContext.NodeType, Token))
                    {
                        return;
                    }
                }
                catch (Exception e) when (e is FabricException || e is TimeoutException)
                {
                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                "MonitorRepairableHealthEventsAsync::HandledException",
                                $"Failure in MonitorRepairableHealthEventAsync::Node:{Environment.NewLine}{e}",
                                Token,
                                null,
                                ConfigSettings.EnableVerboseLogging);
                }

                var unhealthyEvaluations = clusterHealth.UnhealthyEvaluations;

                foreach (var evaluation in unhealthyEvaluations)
                {
                    Token.ThrowIfCancellationRequested();

                    string kind = Enum.GetName(typeof(HealthEvaluationKind), evaluation.Kind);

                    if (kind != null && kind.Contains("Node"))
                    {
                        if (!ConfigSettings.EnableVmRepair && !ConfigSettings.EnableDiskRepair && !ConfigSettings.EnableFabricNodeRepair)
                        {
                            continue;
                        }

                        try
                        {
                            await ProcessMachineHealthAsync(clusterHealth.NodeHealthStates);
                        }
                        catch (Exception e) when (e is FabricException || e is TimeoutException)
                        {
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "MonitorRepairableHealthEventsAsync::HandledException",
                                        $"Failure in MonitorRepairableHealthEventAsync::Node:{Environment.NewLine}{e}",
                                        Token,
                                        null,
                                        ConfigSettings.EnableVerboseLogging);
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
                                    await fabricClient.HealthManager.GetApplicationHealthAsync(
                                            app.ApplicationName, ConfigSettings.AsyncTimeout, Token);

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
                                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                            LogLevel.Info,
                                            "MonitorRepairableHealthEventsAsync::HandledException",
                                            $"Failure in MonitorRepairableHealthEventAsync::Application:{Environment.NewLine}{e}",
                                            Token,
                                            null,
                                            ConfigSettings.EnableVerboseLogging);
                            }
                        }
                    }
                    // FYI: FH currently only supports the case where a replica is stuck. FO does not generate Replica Health Reports.
                    else if (kind != null && kind.Contains("Replica"))
                    {
                        if (!ConfigSettings.EnableReplicaRepair)
                        {
                            continue;
                        }

                        try
                        {
                            await ProcessReplicaHealthAsync(evaluation);
                        }
                        catch (Exception e) when (e is FabricException || e is TimeoutException)
                        {
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "MonitorRepairableHealthEventsAsync::HandledException",
                                        $"Failure in MonitorRepairableHealthEventAsync::Replica:{Environment.NewLine}{e}",
                                        Token,
                                        null,
                                        ConfigSettings.EnableVerboseLogging);
                        }
                    }
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Error,
                            "MonitorRepairableHealthEventsAsync::UnhandledException",
                            $"Failure in MonitorRepairableHealthEventAsync:{Environment.NewLine}{e}",
                            Token,
                            null,
                            ConfigSettings.EnableVerboseLogging);

                RepairLogger.LogWarning($"Unhandled exception in MonitorRepairableHealthEventsAsync:{Environment.NewLine}{e}");

                // Fix the bug(s)..
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
            var nodeList = await fabricClient.QueryManager.GetNodeListAsync(null, ConfigSettings.AsyncTimeout, Token);
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

            appHealth = await fabricClient.HealthManager.GetApplicationHealthAsync(appName, ConfigSettings.AsyncTimeout, Token);

            if (appName.OriginalString != RepairConstants.SystemAppName)
            {
                try
                {
                    var appUpgradeStatus = await fabricClient.ApplicationManager.GetApplicationUpgradeProgressAsync(appName);

                    if (appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingBackInProgress
                        || appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingForwardInProgress
                        || appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingForwardPending)
                    {
                        List<int> udInAppUpgrade = await UpgradeChecker.GetUdsWhereApplicationUpgradeInProgressAsync(fabricClient, appName, Token);
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
                if (!JsonSerializationUtility.TryDeserialize(evt.HealthInformation.Description, out TelemetryData repairData))
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

                    // Only FabricObserver can initiate Service Fabric System service repair. FabricHealerLib does not support this.
                    if (repairData.ObserverName != RepairConstants.FabricSystemObserver)
                    {
                        continue;
                    }

                    // Block attempts to schedule node-level or system service restart repairs if one is already executing in the cluster.
                    var fhRepairTasks = await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token);
                    
                    if (fhRepairTasks.Count > 0)
                    {
                        foreach (var repair in fhRepairTasks)
                        {
                            var executorData = JsonSerializationUtility.TryDeserialize(repair.ExecutorData, out RepairExecutorData exData) ? exData : null;

                            if (executorData?.RepairPolicy?.RepairAction != RepairActionType.RestartFabricNode &&
                                executorData?.RepairPolicy?.RepairAction != RepairActionType.RestartProcess)
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

                    repairId = $"{repairData.NodeName}_{repairData.SystemServiceProcessName}_{repairData.Code}";
                    system = "System ";

                    var currentRepairs =
                        await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token);

                    // Is a repair for the target app service instance already happening in the cluster?
                    // There can be multiple Warnings emitted by FO for a single app at the same time.
                    if (currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(repairData.SystemServiceProcessName)))
                    {
                        var repair = currentRepairs.FirstOrDefault(r => r.ExecutorData.Contains(repairData.SystemServiceProcessName));
                        await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                        LogLevel.Info,
                                                        $"MonitorRepairableHealthEventsAsync::{repairData.SystemServiceProcessName}",
                                                        $"There is already a repair in progress for Fabric system service {repairData.SystemServiceProcessName}(state: {repair.State})",
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

                repairData.RepairId = repairId;
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
            var nodeList = await fabricClient.QueryManager.GetNodeListAsync(null, ConfigSettings.AsyncTimeout, Token);
            ServiceHealth serviceHealth;
            Uri appName;
            Uri serviceName = serviceHealthState.ServiceName;

            // System service target? Do not proceed if system app repair is not enabled.
            if (serviceName.OriginalString.Contains(RepairConstants.SystemAppName) && !ConfigSettings.EnableSystemAppRepair)
            {
                return;
            }

            // User service target? Do not proceed if App repair is not enabled.
            if (!serviceName.OriginalString.Contains(RepairConstants.SystemAppName) && !ConfigSettings.EnableAppRepair)
            {
                return;
            }

            serviceHealth = await fabricClient.HealthManager.GetServiceHealthAsync(serviceName, ConfigSettings.AsyncTimeout, Token);
            var name = await fabricClient.QueryManager.GetApplicationNameAsync(serviceName, ConfigSettings.AsyncTimeout, Token);
            appName = name.ApplicationName;

            if (!serviceName.OriginalString.Contains(RepairConstants.SystemAppName))
            {
                try
                {
                    var app = await fabricClient.QueryManager.GetApplicationNameAsync(serviceName, ConfigSettings.AsyncTimeout, Token);
                    var appUpgradeStatus = await fabricClient.ApplicationManager.GetApplicationUpgradeProgressAsync(app.ApplicationName);

                    if (appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingBackInProgress
                        || appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingForwardInProgress
                        || appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingForwardPending)
                    {
                        List<int> udInAppUpgrade = await UpgradeChecker.GetUdsWhereApplicationUpgradeInProgressAsync(fabricClient, serviceName, Token);
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

            var healthEvents = serviceHealth.HealthEvents;

            if (!healthEvents.Any(h => h.HealthInformation.HealthState == HealthState.Error || h.HealthInformation.HealthState == HealthState.Warning))
            {
                var partitionHealthStates = serviceHealth.PartitionHealthStates.Where(p => p.AggregatedHealthState == HealthState.Warning);

                foreach (var partitionHealthState in partitionHealthStates)
                {
                    var partitionHealth = await fabricClient.HealthManager.GetPartitionHealthAsync(partitionHealthState.PartitionId, ConfigSettings.AsyncTimeout, Token);
                    var replicaHealthStates = partitionHealth.ReplicaHealthStates.Where(p => p.AggregatedHealthState == HealthState.Warning).ToList();

                    if (replicaHealthStates != null && replicaHealthStates.Count > 0)
                    {
                        foreach (var replica in replicaHealthStates)
                        {
                            var replicaHealth =
                                await fabricClient.HealthManager.GetReplicaHealthAsync(partitionHealthState.PartitionId, replica.Id, ConfigSettings.AsyncTimeout, Token);

                            if (replicaHealth != null)
                            {
                                healthEvents = replicaHealth.HealthEvents.Where(h => h.HealthInformation.HealthState == HealthState.Warning).ToList();
                                break;
                            }
                        }
                        break;
                    }
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
                if (!JsonSerializationUtility.TryDeserialize(evt.HealthInformation.Description, out TelemetryData repairData))
                {
                    continue;
                }

                if (repairData.ServiceName.ToLower() != serviceName.OriginalString.ToLower())
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
                            var executorData = JsonSerializationUtility.TryDeserialize(repair.ExecutorData, out RepairExecutorData exData) ? exData : null;

                            if (executorData?.RepairPolicy?.RepairAction != RepairActionType.RestartFabricNode &&
                                executorData?.RepairPolicy?.RepairAction != RepairActionType.RestartProcess)
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
                                                        $"{serviceName} already has a repair in progress for service {repairData.ServiceName}(state: {repair.State})",
                                                        Token,
                                                        null,
                                                        ConfigSettings.EnableVerboseLogging);
                        continue;
                    }
                }

                /* Start repair workflow */

                repairData.RepairId = repairId;
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
                this.repairTaskManager.DetectedHealthEvents.Add(evt);

                // Start the repair workflow.
                await repairTaskManager.StartRepairWorkflowAsync((TelemetryData)repairData, repairRules, Token);
            }
            
        }

        // As far as FabricObserver(FO) is concerned, a node is a VM, so FO only creates NodeHealthReports when a VM level issue
        // is detected. FO does not monitor Fabric node health, but will put a Fabric node in Error or Warning if the underlying 
        // VM is using too much (based on user-supplied threshold value) of some monitored machine resource.
        private async Task ProcessMachineHealthAsync(IEnumerable<NodeHealthState> nodeHealthStates)
        {
            var supportedNodeHealthStates =
                nodeHealthStates.Where(a => a.AggregatedHealthState == HealthState.Warning || a.AggregatedHealthState == HealthState.Error);

            foreach (var node in supportedNodeHealthStates)
            {
                Token.ThrowIfCancellationRequested();

                // Get information about target node.
                var nodeList =
                        await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                    () => fabricClient.QueryManager.GetNodeListAsync(node.NodeName, ConfigSettings.AsyncTimeout, Token),
                                    Token);

                if (nodeList.Count == 0)
                {
                    continue;
                }

                Node targetNode = nodeList[0];

                // Check to see if a VM-level repair is already in flight in the cluster.
                var currentFHVMRepairTasksInProgress =
                            await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(
                                                      $"{RepairTaskEngine.InfrastructureServiceName}/{targetNode.NodeType}",
                                                      Token);

                // FH is very conservative. If there is already a VM or Fabric node-level repair in progress in the cluster, then move on.
                if (currentFHVMRepairTasksInProgress.Count > 0)
                {
                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                 LogLevel.Info,
                                                 "MonitorRepairableHealthEventsAsync::VM_Repair_In_Progress",
                                                 $"There is already a VM-level repair running in the cluster for node type {targetNode.NodeType}. " +
                                                 "Will not do any other VM-level repairs at this time.",
                                                 Token,
                                                 null,
                                                 ConfigSettings.EnableVerboseLogging);
                    continue;
                }
            
                var nodeHealth = await fabricClient.HealthManager.GetNodeHealthAsync(node.NodeName);
                var observerHealthEvents =
                    nodeHealth.HealthEvents.Where(
                                s => (s.HealthInformation.HealthState == HealthState.Warning || s.HealthInformation.HealthState == HealthState.Error));
                
                foreach (var evt in observerHealthEvents)
                {
                    Token.ThrowIfCancellationRequested();

                    // Random wait to limit potential duplicate (concurrent) repair job creation from other FH instances.
                    await RandomWaitAsync();

                    if (string.IsNullOrWhiteSpace(evt.HealthInformation.Description))
                    {
                        continue;
                    }

                    // Check to see if the event Description is a serialized instance of TelemetryData, which would mean the health report was generated in a supported way.
                    if (!JsonSerializationUtility.TryDeserialize(evt.HealthInformation.Description, out TelemetryData repairData))
                    {
                        continue;
                    }

                    // Do not proceed is Disk Repair is not enabled.
                    if (repairData.ObserverName == RepairConstants.DiskObserver && !ConfigSettings.EnableDiskRepair)
                    {
                        continue;
                    }

                    // Do not proceed if VM Repair is not enabled.
                    if (repairData.ObserverName == RepairConstants.NodeObserver && !ConfigSettings.EnableVmRepair)
                    {
                        continue;
                    }

                    // Supported Error code from FabricObserver?
                    if (repairData.ObserverName != null && repairData.Code != null && !SupportedErrorCodes.NodeErrorCodesDictionary.ContainsKey(repairData.Code))
                    {
                        continue;
                    }

                    // FO-only..
                    if (repairData.ObserverName != null && repairData.Code != null)
                    {
                        string errorWarningName = SupportedErrorCodes.GetCodeNameFromErrorCode(repairData.Code);

                        if (string.IsNullOrWhiteSpace(errorWarningName))
                        {
                            continue;
                        }
                    }

                    // FabricHealer only supports VM level repairs that are identified by FabricObserver. FabricHealerLib does not support communicating these types of repairs
                    // to FabricHealer from a non-FO service (TOTHINK: this should change?).
                    if (repairData.ObserverName == null && repairData.EntityType == EntityType.Node)
                    {
                        // FabricHealerLib-generated report, so a restart fabric node request, for example.
                        await ProcessFabricNodeHealthAsync(evt, repairData);
                        continue;
                    }

                    // If there are mulitple instances of FH deployed to the cluster (like -1 InstanceCount), then don't do VM-level repairs if this instance of FH 
                    // detects a need to do so. Another instance on a different node will take the job. Only DiskObserver-generated repair data can be done on the node
                    // where DiskObserver emitted the related information (like Disk space issues and the need to clean specified (in logic rules) folders).
                    if (_instanceCount == -1 && repairData.NodeName == serviceContext.NodeContext.NodeName && repairData.ObserverName != RepairConstants.DiskObserver)
                    {
                        continue;
                    }
                    else if (_instanceCount > 1)
                    {
                        // Disk repair can't take place on any other node than the target node and it has to be same node where FabricObserver (in this case) 
                        // detected the issue.
                        if (repairData.ObserverName == RepairConstants.DiskObserver && repairData.NodeName != serviceContext.NodeContext.NodeName)
                        {
                            continue;
                        }

                        // Randomly wait to decrease chances of simultaneous ownership among FH instances.
                        await RandomWaitAsync();
                    }

                    // Get repair rules for supported source Observer.
                    var repairRules = GetRepairRulesForTelemetryData(repairData);

                    if (repairRules == null || repairRules.Count == 0)
                    {
                        continue;
                    }

                    /* Start repair workflow */

                    string Id = $"VM_Repair_{repairData.Code}{repairData.NodeName}";
                    repairData.RepairId = Id;
                    repairData.Property = evt.HealthInformation.Property;
                    string errOrWarn = "Error";

                    if (evt.HealthInformation.HealthState == HealthState.Warning)
                    {
                        errOrWarn = "Warning";
                    }

                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                 LogLevel.Info,
                                                 $"MonitorRepairableHealthEventsAsync:{Id}",
                                                 $"Detected VM hosting {repairData.NodeName} is in {errOrWarn}. " +
                                                 $"{errOrWarn} Code: {repairData.Code}" +
                                                 $"({SupportedErrorCodes.GetCodeNameFromErrorCode(repairData.Code)})" +
                                                 $"{Environment.NewLine}" +
                                                 $"VM repair policy is enabled. {repairRules.Count} Logic rules found for VM-level repair.",
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

        private async Task ProcessFabricNodeHealthAsync(HealthEvent healthEvent, TelemetryData repairData)
        {
            if (!ConfigSettings.EnableFabricNodeRepair)
            {
                return;
            }

            // This is just used to make sure there is more than 1 node in the cluster.
            var nodeQueryDesc = new NodeQueryDescription
            {
                MaxResults = 3,
            };

            var nodeList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () => fabricClient.QueryManager.GetNodePagedListAsync(
                                                nodeQueryDesc,
                                                ConfigSettings.AsyncTimeout,
                                                Token),
                                        Token);

            if (nodeList?.Count == 1)
            {
                string message = "Fabric node repair is not supported in clusters with 1 node.";

                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"ProcessNodeHealthAsync::Invalid",
                        message,
                        Token,
                        null,
                        ConfigSettings.EnableVerboseLogging);
                return;
            }

            // Block attempts to schedule Fabric node-level repairs if one is already executing in the cluster.
            var fhRepairTasks = await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token);

            if (fhRepairTasks.Count > 0)
            {
                foreach (var repair in fhRepairTasks)
                {
                    var executorData = JsonSerializationUtility.TryDeserialize(repair.ExecutorData, out RepairExecutorData exData) ? exData : null;

                    if (executorData?.RepairPolicy?.RepairAction != RepairActionType.RestartFabricNode &&
                        executorData?.RepairPolicy?.RepairAction != RepairActionType.RestartProcess)
                    {
                        continue;
                    }

                    string message = $"A Service Fabric Node repair ({repair.TaskId}) is already in progress in the cluster(state: {repair.State}). " +
                                     $"Will not attempt repair at this time.";

                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"ProcessNodeHealthAsync::{repair.TaskId}",
                            message,
                            Token,
                            null,
                            ConfigSettings.EnableVerboseLogging);

                    return;
                }
            }

            var repairRules = GetRepairRulesForTelemetryData(repairData);

            if (repairRules == null || repairRules?.Count == 0)
            {
                return;
            }

            // There is only one supported repair for a FabricNode: Restart.
            string repairId = $"{repairData.NodeName}_{repairData.NodeType}_Restart";

            var currentRepairs =
                await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token);

            // Is a repair for the target app service instance already happening in the cluster?
            // There can be multiple Warnings emitted by FO for a single app at the same time.
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

            // Repair already in progress?
            if (currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(repairId)))
            {
                return;
            }

            repairData.RepairId = repairId;
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
                    $"Fabric Node repair is enabled. {repairRules.Count} Logic rules found for Fabric Node-level repair.",
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
        private async Task ProcessReplicaHealthAsync(HealthEvaluation evaluation)
        {
            if (evaluation.Kind != HealthEvaluationKind.Replica)
            {
                return;
            }

            var repUnhealthyEvaluations = ((ReplicaHealthEvaluation)evaluation).UnhealthyEvaluations;

            foreach (var healthEvaluation in repUnhealthyEvaluations)
            {
                Token.ThrowIfCancellationRequested();

                // Random wait to limit potential duplicate (concurrent) repair job creation from other FH instances.
                await RandomWaitAsync(); 

                var eval = (ReplicaHealthEvaluation)healthEvaluation;
                var healthEvent = eval.UnhealthyEvaluations.Cast<EventHealthEvaluation>().FirstOrDefault();
                var service = await fabricClient.QueryManager.GetServiceNameAsync(
                                                                eval.PartitionId,
                                                                ConfigSettings.AsyncTimeout,
                                                                Token);

                /*  Example of repairable problem at Replica level, as health event:

                    [SourceId] ='System.RAP' reported Warning/Error for property...
                    [Property] = 'IStatefulServiceReplica.ChangeRole(N)Duration'.
                    [Description] = The api IStatefulServiceReplica.ChangeRole(N) on node [NodeName] is stuck. 

                    Start Time (UTC): 2020-04-26 19:22:55.492. 
                */

                if (string.IsNullOrWhiteSpace(healthEvent.UnhealthyEvent.HealthInformation.Description))
                {
                    continue;
                }

                if (!healthEvent.UnhealthyEvent.HealthInformation.SourceId.Contains("System.RAP"))
                {
                    continue;
                }

                if (!healthEvent.UnhealthyEvent.HealthInformation.Property.Contains("IStatefulServiceReplica.ChangeRole(N)Duration"))
                {
                    continue;
                }

                if (!healthEvent.UnhealthyEvent.HealthInformation.Description.Contains("stuck"))
                {
                    continue;
                }

                var app = await fabricClient.QueryManager.GetApplicationNameAsync(
                                                            service.ServiceName,
                                                            ConfigSettings.AsyncTimeout,
                                                            Token);

                var replicaList = await fabricClient.QueryManager.GetReplicaListAsync(
                                                                    eval.PartitionId,
                                                                    eval.ReplicaOrInstanceId,
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

                // Since FH runs on each node, have FH only try to repair services that are also running on the same node.
                // This removes the need to try and orchestrate repairs across nodes. The node that is running the target service will also be 
                // running FH.
                if (nodeName != serviceContext.NodeContext.NodeName)
                {
                    continue;
                }

                // Get configuration settings related to Replica repair. 
                var repairRules = GetRepairRulesFromConfiguration(RepairConstants.ReplicaRepairPolicySectionName);

                if (repairRules == null || !repairRules.Any())
                {
                    continue;
                }

                var repairData = new TelemetryData
                {
                    ApplicationName = appName,
                    NodeName = nodeName,
                    ReplicaId = eval.ReplicaOrInstanceId,
                    PartitionId = eval.PartitionId,
                    ServiceName = service.ServiceName.OriginalString,
                    Source = RepairConstants.FabricHealerAppName
                };

                string errOrWarn = "Error";

                if (healthEvent.AggregatedHealthState == HealthState.Warning)
                {
                    errOrWarn = "Warning";
                }

                string repairId = $"ReplicaRepair_{repairData.PartitionId}_{repairData.ReplicaId}";
                repairData.RepairId = repairId;

                // Repair already in progress?
                var currentRepairs = await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token);
                
                if (currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(repairId)))
                {
                    continue;
                }

                /* Start repair workflow */

                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                             LogLevel.Info,
                                             $"MonitorRepairableHealthEventsAsync:Replica_{eval.ReplicaOrInstanceId}_{errOrWarn}",
                                             $"Detected Replica {eval.ReplicaOrInstanceId} on Partition " +
                                             $"{eval.PartitionId} is in {errOrWarn}.{Environment.NewLine}" +
                                             $"Replica repair policy is enabled. " +
                                             $"{repairRules.Count} Logic rules found for Replica repair.",
                                             Token,
                                             null,
                                             ConfigSettings.EnableVerboseLogging);

                // Start the repair workflow.
                await repairTaskManager.StartRepairWorkflowAsync(repairData, repairRules, Token);
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

                    repairPolicySectionName = app == RepairConstants.SystemAppName ? RepairConstants.SystemAppRepairPolicySectionName : RepairConstants.AppRepairPolicySectionName;
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

                    repairPolicySectionName = RepairConstants.VmRepairPolicySectionName;
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
                    repairPolicySectionName = RepairConstants.SystemAppRepairPolicySectionName;
                    break;

                // Disk repair
                case RepairConstants.DiskObserver:
                    repairPolicySectionName = RepairConstants.DiskRepairPolicySectionName;
                    break;

                // VM repair.
                case RepairConstants.NodeObserver:

                    repairPolicySectionName = RepairConstants.VmRepairPolicySectionName;
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

                // System service repair. FO only.
                case EntityType.Application when repairData.SystemServiceProcessName != null:
                    repairPolicySectionName = RepairConstants.SystemAppRepairPolicySectionName;
                    break;

                // Disk repair. Requires FO as originator. (TOTHINK: Why?)
                case EntityType.Node when repairData.ObserverName == RepairConstants.DiskObserver && serviceContext.NodeContext.NodeName == repairData.NodeName:
                    repairPolicySectionName = RepairConstants.DiskRepairPolicySectionName;
                    break;

                // VM repair. Requires FO as originator. (TOTHINK: Why?)
                case EntityType.Node when repairData.Source.Contains(RepairConstants.NodeObserver):
                    repairPolicySectionName = RepairConstants.VmRepairPolicySectionName;
                    break;

                // Fabric Node repair (from FabricHealerLib, for example, where there is no concept of Observer).
                case EntityType.Node when repairData.ObserverName == null && repairData.NodeName != null && repairData.NodeType != null:
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
                string logicRulesConfigFileName = GetSettingParameterValue(
                                                     serviceContext,
                                                     repairPolicySectionName,
                                                     RepairConstants.LogicRulesConfigurationFile);

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
                fabricClient?.Dispose();
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

                if (rules[ptr2].EndsWith("."))
                {
                    if (ptr1 == ptr2)
                    {
                        repairRules.Add(rules[ptr2].Remove(rules[ptr2].Length - 1, 1));
                    }
                    else
                    {
                        string rule = rules[ptr1].TrimEnd(' ');

                        for (int i = ptr1 + 1; i <= ptr2; i++)
                        {
                            rule = rule + ' ' + rules[i].Replace('\t', ' ').TrimStart(' ');
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
                                                HealthReportType.Application); 
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
                var healthReporter = new FabricHealthReporter(fabricClient);
                var healthReport = new HealthReport
                {
                    HealthMessage = "Clearing existing health reports as FabricHealer is stopping or updating.",
                    NodeName = serviceContext.NodeContext.NodeName,
                    State = HealthState.Ok,
                    HealthReportTimeToLive = TimeSpan.FromMinutes(5),
                };

                var appName = new Uri(RepairConstants.FabricHealerAppName);
                var appHealth = await fabricClient.HealthManager.GetApplicationHealthAsync(appName);
                var FHAppEvents = appHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(RepairConstants.FabricHealer));

                foreach (HealthEvent evt in FHAppEvents)
                {
                    healthReport.AppName = appName;
                    healthReport.Property = evt.HealthInformation.Property;
                    healthReport.SourceId = evt.HealthInformation.SourceId;
                    healthReport.ReportType = HealthReportType.Application;

                    healthReporter.ReportHealthToServiceFabric(healthReport);
                    Thread.Sleep(50);
                }

                var nodeHealth = await fabricClient.HealthManager.GetNodeHealthAsync(serviceContext.NodeContext.NodeName);
                var FHNodeEvents = nodeHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(RepairConstants.FabricHealer));

                foreach (HealthEvent evt in FHNodeEvents)
                {
                    healthReport.Property = evt.HealthInformation.Property;
                    healthReport.SourceId = evt.HealthInformation.SourceId;
                    healthReport.ReportType = HealthReportType.Node;

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
