﻿// ------------------------------------------------------------
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

namespace FabricHealer
{
    public sealed class FabricHealerManager : IDisposable
    {
        internal static TelemetryUtilities TelemetryUtilities;
        internal static RepairData RepairHistory;

        // Folks often use their own version numbers. This is for internal diagnostic telemetry.
        private const string InternalVersionNumber = "1.0.0-Preview";
        private static FabricHealerManager singleton;
        private bool disposedValue;
        private readonly StatelessServiceContext serviceContext;
        private readonly FabricClient fabricClient;
        private readonly RepairTaskManager repairTaskManager;
        private readonly RepairTaskEngine repairTaskEngine;
        private readonly Uri systemAppUri = new Uri("fabric:/System");
        private readonly Uri repairManagerServiceUri = new Uri("fabric:/System/RepairManagerService");
        private readonly FabricHealthReporter healthReporter;
        private readonly TimeSpan OperationalTelemetryRunInterval = TimeSpan.FromDays(1);
        private int nodeCount;
        private DateTime StartDateTime;

        internal static Logger RepairLogger
        {
            get;
            private set;
        }

        internal static ConfigSettings ConfigSettings
        {
            get;
            private set;
        }

        private bool FabricHealerOperationalTelemetryEnabled
        {
            get; set;
        }

        // CancellationToken from FabricHealer.RunAsync. See ctor.
        private CancellationToken Token
        {
            get;
        }

        private DateTime LastTelemetrySendDate
        {
            get; set;
        }

        private bool EtwEnabled 
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
            RepairLogger = new Logger("FabricHealer", ConfigSettings.LocalLogPathParameter)
            {
                EnableVerboseLogging = ConfigSettings.EnableVerboseLogging
            };

            RepairHistory = new RepairData();
            healthReporter = new FabricHealthReporter(fabricClient);
        }

        /// <summary>
        /// This is the static singleton instance of FabricHealerManager type. FabricHealerManager does not support
        /// multiple instantiations. It does not provide a public constructor.
        /// </summary>
        /// <param name="context">StatefulService context instance.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The singleton instance of FabricHealerManager.</returns>
        public static FabricHealerManager Singleton(StatelessServiceContext context, CancellationToken token)
        {
            return singleton ??= new FabricHealerManager(context ?? throw new ArgumentException("context can't be null"), token);
        }

        /// <summary>
        /// Checks if repair manager is enabled in the cluster or not
        /// </summary>
        /// <param name="serviceNameFabricUri"></param>
        /// <param name="cancellationToken">cancellation token to stop the async operation</param>
        /// <returns>true if repair manager application is present in cluster, otherwise false</returns>
        private async Task<bool> CheckRepairManagerDeploymentStatusAsync(Uri serviceNameFabricUri, CancellationToken cancellationToken)
        {
            string okMessage = $"{serviceNameFabricUri} is deployed.";
            bool isRmDeployed = true;

            var healthReport = new HealthReport
            {
                NodeName = serviceContext.NodeContext.NodeName,
                AppName = new Uri(serviceContext.CodePackageActivationContext.ApplicationName),
                ReportType = HealthReportType.Application,
                HealthMessage = okMessage,
                State = HealthState.Ok,
                Property = "RequirementCheck::RMDeployed",
                HealthReportTimeToLive = TimeSpan.FromMinutes(5),
                Source = "FabricHealer",
            };

            var serviceList = await fabricClient.QueryManager.GetServiceListAsync(
                                      systemAppUri,
                                      serviceNameFabricUri,
                                      ConfigSettings.AsyncTimeout,
                                      cancellationToken).ConfigureAwait(true);

            if ((serviceList?.Count ?? 0) == 0)
            {
                var warnMessage =
                    $"{repairManagerServiceUri} could not be found, " +
                    $"FabricHealer Service requires {repairManagerServiceUri} system service to be deployed in the cluster. " +
                    "Consider adding a RepairManager section in your cluster manifest.";
                healthReport.HealthMessage = warnMessage;
                healthReport.State = HealthState.Warning;
                healthReport.Code = FOErrorWarningCodes.Ok;
                healthReport.HealthReportTimeToLive = TimeSpan.MaxValue;
                healthReport.Source = "CheckRepairManagerDeploymentStatusAsync";
                isRmDeployed = false;
            }

            healthReporter.ReportHealthToServiceFabric(healthReport);
            return isRmDeployed;
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

            if (!ConfigSettings.EnableAutoMitigation || !await CheckRepairManagerDeploymentStatusAsync(repairManagerServiceUri, Token).ConfigureAwait(true))
            {
                return;
            }

            try
            {
                RepairLogger.LogInfo("Starting FabricHealer Health Detection loop.");

                var nodeList =
                   await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                  () => fabricClient.QueryManager.GetNodeListAsync(null, ConfigSettings.AsyncTimeout, Token),
                                                  Token).ConfigureAwait(true);
                nodeCount = nodeList.Count;

                // First, let's clean up any orphan non-node level FabricHealer repair tasks left pending 
                // when the FabricHealer process is killed or otherwise ungracefully closed.
                // This call will return quickly if FH was gracefully closed as there will be
                // no outstanding repair tasks left orphaned.
                await CancelOrResumeAllRunningFHRepairsAsync().ConfigureAwait(true);

                // Run until RunAsync token is canceled.
                while (!Token.IsCancellationRequested)
                {
                    if (!ConfigSettings.EnableAutoMitigation)
                    {
                        break;
                    }

                    _ = await MonitorRepairableHealthEventsAsync().ConfigureAwait(true);
                    
                    // Identity-agnostic internal operational telemetry sent to Service Fabric team (only) for use in
                    // understanding generic behavior of FH in the real world (no PII). This data is sent once a day and will be retained for no more
                    // than 90 days.
                    if (ConfigSettings.OperationalTelemetryEnabled && DateTime.UtcNow.Subtract(LastTelemetrySendDate) >= OperationalTelemetryRunInterval)
                    {
                        try
                        {
                            using var telemetryEvents = new TelemetryEvents(
                                                                fabricClient,
                                                                serviceContext,
                                                                ServiceEventSource.Current,
                                                                Token,
                                                                EtwEnabled);

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

                    await Task.Delay(
                        TimeSpan.FromSeconds(
                            ConfigSettings.ExecutionLoopSleepSeconds > 0 ? ConfigSettings.ExecutionLoopSleepSeconds : 10), Token).ConfigureAwait(true);      
                }

                // Clean up, close down.
                RepairLogger.LogInfo("Shutdown signaled. Stopping.");
            }
            catch (AggregateException)
            {
                    // This check is necessary to prevent cancelling outstanding repair tasks if 
                    // one of the handled exceptions originated from another operation unrelated to
                    // shutdown (like an async operation that timed out).
                    if (Token.IsCancellationRequested)
                    {
                        RepairLogger.LogInfo("Shutdown signaled. Stopping.");
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
                }
            }
            catch (Exception e)
            {
                var message = $"Unhandeld Exception in FabricHealerManager:{Environment.NewLine}{e}";
                RepairLogger.LogError(message);
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(LogLevel.Warning, "FabricHealer", message, Token).ConfigureAwait(true);

                // ETW.
                if (ConfigSettings.EtwEnabled)
                {
                    ServiceEventSource.Current.Write(
                                        RepairConstants.EventSourceEventName,
                                        new
                                        {
                                            HealthState = "Warning",
                                            Node = serviceContext.NodeContext.NodeName,
                                            Source = "FabricHealer.FabricHealerManager",
                                            Value = message,
                                        });
                }

                // Operational telemetry sent to FO developer for use in understanding generic behavior of FO in the real world (no PII)
                if (ConfigSettings.OperationalTelemetryEnabled)
                {
                    try
                    {
                        using var telemetryEvents = new TelemetryEvents(
                                                            fabricClient,
                                                            serviceContext,
                                                            ServiceEventSource.Current,
                                                            Token,
                                                            EtwEnabled);

                        var fhData = new FabricHealerCriticalErrorEventData
                        {
                            Source = nameof(FabricHealerManager),
                            ErrorMessage = e.Message,
                            ErrorStack = e.StackTrace,
                            CrashTime = DateTime.UtcNow.ToString("o"),
                            Version = InternalVersionNumber
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
                    RepairData = RepairHistory
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
                                                           Token), Token).ConfigureAwait(true);

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
                        await FabricRepairTasks.CancelRepairTaskAsync(repair, fabricClient).ConfigureAwait(true);
                    }

                    /* Resume interrupted Fabric Node restart repairs */

                    // There is no need to resume simple repairs that do not require multiple repair steps (e.g., codepackage/process/replica restarts).
                    if (repairExecutorData.RepairPolicy.RepairAction != RepairActionType.RestartFabricNode)
                    {
                        continue;
                    }

                    string errorCode = repairExecutorData.FOErrorCode;
                    
                    if (string.IsNullOrWhiteSpace(errorCode))
                    {
                        continue;
                    }

                    // File Deletion repair is a node-level (VM) repair, but is not multi-step. Ignore.
                    if (FOErrorWarningCodes.GetErrorWarningNameFromCode(errorCode).Contains("Disk"))
                    {
                        continue;
                    }

                    // Fabric System service warnings/errors from FO can be Node level repair targets (e.g., Fabric binary needs to be restarted).
                    // FH will restart the node hosting the troubled SF system service if specified in related logic rules.
                    var repairRules = GetRepairRulesFromConfiguration(!string.IsNullOrWhiteSpace(repairExecutorData.SystemServiceProcessName) ? RepairConstants.SystemAppRepairPolicySectionName : RepairConstants.FabricNodeRepairPolicySectionName);

                    var foHealthData = new TelemetryData
                    {
                        NodeName = repairExecutorData.NodeName,
                        Code = errorCode,
                    };

                    _ = await repairTaskManager.InitializeGuanAndRunQuery(foHealthData, repairRules, repairExecutorData).ConfigureAwait(true);
                }
            }
            catch (Exception e) when (
                    e is FabricException ||
                    e is OperationCanceledException ||
                    e is TimeoutException)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                             LogLevel.Info,
                                             "CancelOrResumeAllRunningFHRepairsAsync",
                                             $"Could not cancel or resume repair tasks. Failed with:{Environment.NewLine}{e}",
                                             Token).ConfigureAwait(true);
            }
        }

        private void CodePackageActivationContext_ConfigurationPackageModifiedEvent(object sender, PackageModifiedEventArgs<ConfigurationPackage> e)
        {
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

        private async Task<bool> MonitorRepairableHealthEventsAsync()
        {
            try
            {
                var clusterHealth = await fabricClient.HealthManager.GetClusterHealthAsync(ConfigSettings.AsyncTimeout, Token).ConfigureAwait(true);

                if (clusterHealth.AggregatedHealthState == HealthState.Ok)
                {
                    return true;
                }

                // Check cluster upgrade status. If the cluster is upgrading to a new version (or rolling back)
                // then do not attempt repairs.
                int udInClusterUpgrade = await UpgradeChecker.GetUdsWhereFabricUpgradeInProgressAsync(fabricClient, Token).ConfigureAwait(true);

                if (udInClusterUpgrade > -1)
                {
                    string telemetryDescription = $"Cluster is currently upgrading in UD {udInClusterUpgrade}. Will not schedule or execute repairs at this time.";
                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                 LogLevel.Info,
                                                 "MonitorRepairableHealthEventsAsync::ClusterUpgradeDetected",
                                                 telemetryDescription,
                                                 Token).ConfigureAwait(true);
                    return true;
                }

                // Check to see if an Azure tenant update is in progress. Do not conduct repairs if so.
                if (await UpgradeChecker.IsAzureTenantUpdateInProgress(
                                            fabricClient,
                                            serviceContext.NodeContext.NodeType,
                                            Token).ConfigureAwait(true))
                {
                    return true;
                }

                var unhealthyEvaluations = clusterHealth.UnhealthyEvaluations;

                foreach (var evaluation in unhealthyEvaluations)
                {
                    Token.ThrowIfCancellationRequested();

                    string kind = Enum.GetName(typeof(HealthEvaluationKind), evaluation.Kind);

                    if (kind != null && kind.Contains("Node"))
                    {
                        if (!ConfigSettings.EnableVmRepair && !ConfigSettings.EnableDiskRepair)
                        {
                            continue;
                        }

                        try
                        {
                            await ProcessNodeHealthAsync(clusterHealth.NodeHealthStates).ConfigureAwait(true);
                        }
                        catch (Exception e) when (
                                e is FabricException ||
                                e is OperationCanceledException ||
                                e is TaskCanceledException ||
                                e is TimeoutException)
                        {
#if DEBUG
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                         LogLevel.Info,
                                                         "MonitorRepairableHealthEventsAsync::HandledException",
                                                         $"Failure in MonitorRepairableHealthEventAsync::Node:{Environment.NewLine}{e}",
                                                         Token);
#endif
                        }
                    }
                    else if (kind != null && kind.Contains("Application"))
                    {
                        if (!ConfigSettings.EnableAppRepair && !ConfigSettings.EnableSystemAppRepair)
                        {
                            continue;
                        }

                        try
                        {
                            await ProcessApplicationHealthAsync(clusterHealth.ApplicationHealthStates).ConfigureAwait(true);
                        }
                        catch (Exception e) when (
                                e is FabricException ||
                                e is OperationCanceledException ||
                                e is TaskCanceledException ||
                                e is TimeoutException)
                        {
#if DEBUG
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                         LogLevel.Info,
                                                         "MonitorRepairableHealthEventsAsync::HandledException",
                                                         $"Failure in MonitorRepairableHealthEventAsync::Application:{Environment.NewLine}{e}",
                                                         Token);
#endif
                        }
                    }
                    // FYI: FH currently only supports the case where a replica is stuck. FO does not emit ReplicaHealthReports.
                    else if (kind != null && kind.Contains("Replica"))
                    {
                        if (!ConfigSettings.EnableReplicaRepair)
                        {
                            continue;
                        }

                        try
                        {
                            await ProcessReplicaHealthAsync(evaluation).ConfigureAwait(true);
                        }
                        catch (Exception e) when (
                                e is FabricException ||
                                e is TimeoutException ||
                                e is TaskCanceledException ||
                                e is OperationCanceledException)
                        {
#if DEBUG
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                         LogLevel.Info,
                                                         "MonitorRepairableHealthEventsAsync::HandledException",
                                                         $"Failure in MonitorRepairableHealthEventAsync::Replica:{Environment.NewLine}{e}",
                                                         Token);
#endif
                        }
                    }
                    else
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception e) when (e is FabricException || e is OperationCanceledException || e is TimeoutException)
            {
                return false;
            }
            catch (Exception e)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                             LogLevel.Error,
                                             "MonitorRepairableHealthEventsAsync::UnhandledException",
                                             $"Failure in MonitorRepairableHealthEventAsync:{Environment.NewLine}{e}",
                                             Token);

                RepairLogger.LogWarning($"Unhandled exception in MonitorRepairableHealthEventsAsync:{Environment.NewLine}{e}");

                // Fix the bug(s)..
                throw;
            }
        }

        private async Task ProcessApplicationHealthAsync(IEnumerable<ApplicationHealthState> appHealthStates)
        {
            var supportedAppHealthStates = appHealthStates.Where(a => a.AggregatedHealthState == HealthState.Warning || a.AggregatedHealthState == HealthState.Error);
            var nodeList = await fabricClient.QueryManager.GetNodeListAsync().ConfigureAwait(false);

            // Random wait to limit potential duplicate (concurrent) repair job creation from other FH instances.
            await RandomWait();

            foreach (var app in supportedAppHealthStates)
            {
                Token.ThrowIfCancellationRequested();

                var appHealth = await fabricClient.HealthManager.GetApplicationHealthAsync(app.ApplicationName).ConfigureAwait(true);
                var appName = app.ApplicationName;

                if (appName.OriginalString != "fabric:/System")
                {
                    var appUpgradeStatus = await fabricClient.ApplicationManager.GetApplicationUpgradeProgressAsync(appName).ConfigureAwait(true);

                    if (appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingBackInProgress
                        || appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingForwardInProgress
                        || appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingForwardPending)
                    {
                        List<int> udInAppUpgrade = await UpgradeChecker.GetUdsWhereApplicationUpgradeInProgressAsync(fabricClient, appName, Token).ConfigureAwait(true);
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
                                                     Token).ConfigureAwait(true);
                        continue;
                    }
                }

                var observerHealthEvents = appHealth.HealthEvents.Where(
                                              s => s.HealthInformation.SourceId.ToLower().Contains("observer")
                                                   && (s.HealthInformation.HealthState == HealthState.Warning
                                                       || s.HealthInformation.HealthState == HealthState.Error));

                foreach (var evt in observerHealthEvents)
                {
                    Token.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(evt.HealthInformation.Description))
                    {
                        continue;
                    }

                    // If source of health data is not FabricObserver, move on.
                    if (!JsonSerializationUtility.TryDeserialize(evt.HealthInformation.Description, out TelemetryData foHealthData))
                    {
                        continue;
                    }

                    if (!FOErrorWarningCodes.AppErrorCodesDictionary.ContainsKey(foHealthData.Code)
                        || foHealthData.Code == FOErrorWarningCodes.AppErrorNetworkEndpointUnreachable
                        || foHealthData.Code == FOErrorWarningCodes.AppWarningNetworkEndpointUnreachable)
                    {
                        // Network endpoint test failures have no general mitigation.
                        continue;
                    }

                    // Get configuration settings related to Application (service code package) repair.
                    List<string> repairRules;
                    string repairId;
                    string system = string.Empty;

                    if (app.ApplicationName.OriginalString == "fabric:/System")
                    {
                        // Block attempts to schedule node-level or system service restart repairs if one is already executing in the cluster.
                        var fhRepairTasks = await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(
                                                                     RepairTaskEngine.FabricHealerExecutorName,
                                                                     Token).ConfigureAwait(true);

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

                                string message = $"A Service Fabric System service repair ({repair.TaskId}) is already in progress in the cluster. " +
                                                 $"Will not attempt repair at this time.";

                                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                             LogLevel.Info,
                                                             $"ProcessApplicationHealth::System::{repair.TaskId}",
                                                             message,
                                                             Token).ConfigureAwait(true);
                                return;
                            }
                        }

                        repairRules = GetRepairRulesFromFOCode(foHealthData.Code, "fabric:/System");

                        if (repairRules == null || repairRules?.Count == 0)
                        {
                            continue;
                        }

                        repairId = $"{foHealthData.NodeName}_{foHealthData.SystemServiceProcessName}_{foHealthData.Code}";
                        system = "System ";

                        var currentRepairs =
                            await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token).ConfigureAwait(true);

                        // Is a repair for the target app service instance already happening in the cluster?
                        // There can be multiple Warnings emitted by FO for a single app at the same time.
                        if (currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(foHealthData.SystemServiceProcessName)))
                        {
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                         LogLevel.Info,
                                                         $"MonitorRepairableHealthEventsAsync::{foHealthData.SystemServiceProcessName}",
                                                         $"There is already a repair in progress for Fabric system service {foHealthData.SystemServiceProcessName}",
                                                         Token).ConfigureAwait(true);
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
                        repairRules = GetRepairRulesFromFOCode(foHealthData.Code);

                        // Nothing to do here.
                        if (repairRules == null || repairRules?.Count == 0)
                        {
                            continue;
                        }

                        // Don't restart thyself.
                        if (foHealthData.ServiceName == serviceContext.ServiceName.OriginalString && foHealthData.NodeName == serviceContext.NodeContext.NodeName)
                        {
                            continue;
                        }

                        string serviceProcessName = $"{foHealthData.ServiceName?.Replace("fabric:/", "").Replace("/", "")}";
                        var currentRepairs =
                            await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token).ConfigureAwait(true);

                        // This is the way each FH repair is ID'd. This data is stored in the related Repair Task's ExecutorData property.
                        repairId = $"{foHealthData.NodeName}_{serviceProcessName}_{foHealthData.Metric?.Replace(" ", string.Empty)}";

                        // Is a repair for the target app service instance already happening in the cluster?
                        // There can be multiple Warnings emitted by FO for a single app at the same time.
                        if (currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(repairId)))
                        {
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                         LogLevel.Info,
                                                         $"MonitorRepairableHealthEventsAsync::{foHealthData.ServiceName}",
                                                         $"{appName} already has a repair in progress for service {foHealthData.ServiceName}",
                                                         Token).ConfigureAwait(true);
                            continue;
                        }
                    }

                    foHealthData.RepairId = repairId;
                    foHealthData.HealthEventProperty = evt.HealthInformation.Property;
                    string errOrWarn = "Error";

                    if (evt.HealthInformation.HealthState == HealthState.Warning)
                    {
                        errOrWarn = "Warning";
                    }

                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                 LogLevel.Info,
                                                 $"MonitorRepairableHealthEventsAsync:{repairId}",
                                                 $"Detected {errOrWarn} state for Application {foHealthData.ApplicationName}{Environment.NewLine}" +
                                                 $"SourceId: {evt.HealthInformation.SourceId}{Environment.NewLine}" +
                                                 $"Property: {evt.HealthInformation.Property}{Environment.NewLine}" +
                                                 $"{system}Application repair policy is enabled. " +
                                                 $"{repairRules.Count} Logic rules found for {system}Application-level repair.",
                                                 Token).ConfigureAwait(true);

                    // Update the in-memory HealthEvent List.
                    this.repairTaskManager.DetectedHealthEvents.Add(evt);

                    // Start the repair workflow.
                    await repairTaskManager.StartRepairWorkflowAsync(foHealthData, repairRules, Token).ConfigureAwait(true);
                }
            }
        }

        // As far as FabricObserver(FO) is concerned, a node is a VM, so FO only creates NodeHealthReports when a VM level issue
        // is detected. FO does not monitor Fabric node health, but will put a Fabric node in Error or Warning if the underlying 
        // VM is using too much (based on user-supplied threshold value) of some monitored machine resource.
        private async Task ProcessNodeHealthAsync(IEnumerable<NodeHealthState> nodeHealthStates)
        {
            // Random wait to limit potential duplicate (concurrent) repair job creation from other FH instances.
            await RandomWait();

            var supportedNodeHealthStates = nodeHealthStates.Where(a => a.AggregatedHealthState == HealthState.Warning || a.AggregatedHealthState == HealthState.Error);

            foreach (var node in supportedNodeHealthStates)
            {
                Token.ThrowIfCancellationRequested();

                // Get information about target node.
                var nodeList =
                        await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                       () => fabricClient.QueryManager.GetNodeListAsync(node.NodeName, ConfigSettings.AsyncTimeout, Token),
                                                       Token).ConfigureAwait(true);
                if (nodeList.Count == 0)
                {
                    continue;
                }

                Node targetNode = nodeList[0];

                // Check to see if a VM-level repair is already in flight in the cluster.
                var currentFHVMRepairTasksInProgress =
                            await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(
                                                      $"{RepairTaskEngine.InfrastructureServiceName}/{targetNode.NodeType}",
                                                      Token).ConfigureAwait(true);

                // FH is very conservative. If there is already a VM repair in progress in the cluster, then move on.
                if (currentFHVMRepairTasksInProgress.Count > 0)
                {
                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                 LogLevel.Info,
                                                 "MonitorRepairableHealthEventsAsync::VM_Repair_In_Progress",
                                                 "There is already a VM-level repair running in the cluster. Will not do any VM repairs at this time.",
                                                 Token).ConfigureAwait(true);
                    continue;
                }
            
                var nodeHealth = await fabricClient.HealthManager.GetNodeHealthAsync(node.NodeName).ConfigureAwait(true);
                var observerHealthEvents = nodeHealth.HealthEvents.Where(
                                            s => s.HealthInformation.SourceId.ToLower().Contains("observer")
                                                 && (s.HealthInformation.HealthState == HealthState.Warning 
                                                     || s.HealthInformation.HealthState == HealthState.Error));
                
                foreach (var evt in observerHealthEvents)
                {
                    Token.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(evt.HealthInformation.Description))
                    {
                        continue;
                    }

                    // From FabricObserver? FO creates health events with HealthInformation.Description properties
                    // containing serialized instances of TelemetryData type. FH only works with FO-supplied health data.
                    if (!JsonSerializationUtility.TryDeserialize(evt.HealthInformation.Description, out TelemetryData foHealthData))
                    {
                        continue;
                    }

                    // Supported Error code from FO?
                    if (!FOErrorWarningCodes.NodeErrorCodesDictionary.ContainsKey(foHealthData.Code))
                    {
                        continue;
                    }

                    string errorWarningName = FOErrorWarningCodes.GetErrorWarningNameFromCode(foHealthData.Code);
                    
                    if (string.IsNullOrWhiteSpace(errorWarningName))
                    {
                        continue;
                    }

                    // Don't try any VM-level repairs - except for Disk repair - if the target VM is the same one where this instance of FH is running.
                    // Another FH instance on a different VM will run repair.
                    if (foHealthData.NodeName == serviceContext.NodeContext.NodeName)
                    {
                        // Disk file maintenance (deletion) can only take place on the same node where the disk space issue was detected by FO.
                        if (!errorWarningName.Contains("Disk"))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        // FH can't clean directories on other VMs in the cluster.
                        if (errorWarningName.Contains("Disk"))
                        {
                            continue;
                        }
                    }

                    // Get configuration settings related to supported Node repair.
                    var repairRules = GetRepairRulesFromFOCode(foHealthData.Code);

                    if (repairRules == null || repairRules.Count == 0)
                    {
                        continue;
                    }

                    string Id = $"VM_Repair_{foHealthData.Code}{foHealthData.NodeName}";
                    foHealthData.RepairId = Id;
                    foHealthData.HealthEventProperty = evt.HealthInformation.Property;
                    string errOrWarn = "Error";

                    if (evt.HealthInformation.HealthState == HealthState.Warning)
                    {
                        errOrWarn = "Warning";
                    }

                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                 LogLevel.Info,
                                                 $"MonitorRepairableHealthEventsAsync:{Id}",
                                                 $"Detected VM hosting {foHealthData.NodeName} is in {errOrWarn}. " +
                                                 $"{errOrWarn} Code: {foHealthData.Code}" +
                                                 $"({FOErrorWarningCodes.GetErrorWarningNameFromCode(foHealthData.Code)})" +
                                                 $"{Environment.NewLine}" +
                                                 $"VM repair policy is enabled. {repairRules.Count} Logic rules found for VM-level repair.",
                                                 Token).ConfigureAwait(true);
                    
                    // Update the in-memory HealthEvent List.
                    this.repairTaskManager.DetectedHealthEvents.Add(evt);

                    // Start the repair workflow.
                    await repairTaskManager.StartRepairWorkflowAsync(foHealthData, repairRules, Token).ConfigureAwait(true);
                }
            }
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

            // Random wait to limit potential duplicate (concurrent) repair job creation from other FH instances.
            await RandomWait();

            var repUnhealthyEvaluations = ((ReplicaHealthEvaluation)evaluation).UnhealthyEvaluations;

            foreach (var healthEvaluation in repUnhealthyEvaluations)
            {
                var eval = (ReplicaHealthEvaluation)healthEvaluation;
                var healthEvent = eval.UnhealthyEvaluations.Cast<EventHealthEvaluation>().FirstOrDefault();

                Token.ThrowIfCancellationRequested();

                var service = await fabricClient.QueryManager.GetServiceNameAsync(
                                                                eval.PartitionId,
                                                                ConfigSettings.AsyncTimeout,
                                                                Token).ConfigureAwait(true);

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
                                                            Token).ConfigureAwait(true);

                var replicaList = await fabricClient.QueryManager.GetReplicaListAsync(
                                                                    eval.PartitionId,
                                                                    eval.ReplicaOrInstanceId,
                                                                    ConfigSettings.AsyncTimeout,
                                                                    Token).ConfigureAwait(true);
                // Replica still exist?
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

                var foHealthData = new TelemetryData
                {
                    ApplicationName = appName,
                    NodeName = nodeName,
                    ReplicaId = eval.ReplicaOrInstanceId,
                    PartitionId = eval.PartitionId.ToString(),
                    ServiceName = service.ServiceName.OriginalString,
                };

                string errOrWarn = "Error";

                if (healthEvent.AggregatedHealthState == HealthState.Warning)
                {
                    errOrWarn = "Warning";
                }

                string repairId = $"ReplicaRepair_{foHealthData.PartitionId}_{foHealthData.ReplicaId}";
                foHealthData.RepairId = repairId;

                // Repair already in progress?
                var currentRepairs = await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token).ConfigureAwait(true);
                
                if (currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(repairId)))
                {
                    continue;
                }

                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                             LogLevel.Info,
                                             $"MonitorRepairableHealthEventsAsync:Replica_{eval.ReplicaOrInstanceId}_{errOrWarn}",
                                             $"Detected Replica {eval.ReplicaOrInstanceId} on Partition " +
                                             $"{eval.PartitionId} is in {errOrWarn}.{Environment.NewLine}" +
                                             $"Replica repair policy is enabled. " +
                                             $"{repairRules.Count} Logic rules found for unhealthy Replica repair.",
                                             Token).ConfigureAwait(true);

                // Start the repair workflow.
                await repairTaskManager.StartRepairWorkflowAsync(foHealthData, repairRules, Token).ConfigureAwait(true);
            }
        }
        
        private List<string> GetRepairRulesFromFOCode(string foErrorCode, string app = null)
        {
            if (!FOErrorWarningCodes.AppErrorCodesDictionary.ContainsKey(foErrorCode)
                && !FOErrorWarningCodes.NodeErrorCodesDictionary.ContainsKey(foErrorCode))
            {
                return null;
            }

            string repairPolicySectionName;

            switch (foErrorCode)
            {
                // App level.
                case FOErrorWarningCodes.AppErrorCpuPercent:
                case FOErrorWarningCodes.AppErrorMemoryMB:
                case FOErrorWarningCodes.AppErrorMemoryPercent:
                case FOErrorWarningCodes.AppErrorTooManyActiveEphemeralPorts:
                case FOErrorWarningCodes.AppErrorTooManyActiveTcpPorts:
                case FOErrorWarningCodes.AppErrorTooManyOpenFileHandles:
                case FOErrorWarningCodes.AppErrorTooManyThreads:
                case FOErrorWarningCodes.AppWarningCpuPercent:
                case FOErrorWarningCodes.AppWarningMemoryMB:
                case FOErrorWarningCodes.AppWarningMemoryPercent:
                case FOErrorWarningCodes.AppWarningTooManyActiveEphemeralPorts:
                case FOErrorWarningCodes.AppWarningTooManyActiveTcpPorts:
                case FOErrorWarningCodes.AppWarningTooManyOpenFileHandles:
                case FOErrorWarningCodes.AppWarningTooManyThreads:

                    repairPolicySectionName = app == "fabric:/System" ? RepairConstants.SystemAppRepairPolicySectionName : RepairConstants.AppRepairPolicySectionName;
                    break;

                // Node level. (node = VM, not Fabric node)
                case FOErrorWarningCodes.NodeErrorCpuPercent:
                case FOErrorWarningCodes.NodeErrorMemoryMB:
                case FOErrorWarningCodes.NodeErrorMemoryPercent:
                case FOErrorWarningCodes.NodeErrorTooManyActiveEphemeralPorts:
                case FOErrorWarningCodes.NodeErrorTooManyActiveTcpPorts:
                case FOErrorWarningCodes.NodeErrorTotalOpenFileHandlesPercent:
                case FOErrorWarningCodes.NodeWarningCpuPercent:
                case FOErrorWarningCodes.NodeWarningMemoryMB:
                case FOErrorWarningCodes.NodeWarningMemoryPercent:
                case FOErrorWarningCodes.NodeWarningTooManyActiveEphemeralPorts:
                case FOErrorWarningCodes.NodeWarningTooManyActiveTcpPorts:
                case FOErrorWarningCodes.NodeWarningTotalOpenFileHandlesPercent:

                    repairPolicySectionName = RepairConstants.VmRepairPolicySectionName;
                    break;

                case FOErrorWarningCodes.NodeWarningDiskSpaceMB:
                case FOErrorWarningCodes.NodeErrorDiskSpaceMB:
                case FOErrorWarningCodes.NodeWarningDiskSpacePercent:
                case FOErrorWarningCodes.NodeErrorDiskSpacePercent:

                    repairPolicySectionName = RepairConstants.DiskRepairPolicySectionName;
                    break;

                default:
                    return null;
            }

            return GetRepairRulesFromConfiguration(repairPolicySectionName);
        }

        private List<string> GetRepairRulesFromConfiguration(string repairPolicySectionName)
        {
            // Get config filename and read lines from file.
            string logicRulesConfigFileName = GetSettingParameterValue(
                                                 serviceContext,
                                                 repairPolicySectionName,
                                                 RepairConstants.LogicRulesConfigurationFile);

            var configPath = serviceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Path;
            var rulesFolderPath = Path.Combine(configPath, RepairConstants.LogicRulesFolderName);
            var rulesFilePath = Path.Combine(rulesFolderPath, logicRulesConfigFileName);
            List<string> rules = File.ReadAllLines(rulesFilePath).ToList();
            List<string> repairRules = ParseRulesFile(rules);

            return repairRules;
        }

        private int GetEnabledRepairRuleCount()
        {
            var config = serviceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config");
            int count = 0;

            foreach (var section in config.Settings.Sections)
            {
                if (!section.Name.Contains("RepairPolicy"))
                {
                    continue;
                }

                if (section.Parameters["Enabled"]?.Value?.ToLower() == "true")
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

        private static List<string> ParseRulesFile(List<string> rules)
        {
            var repairRules = new List<string>();
            int ptr1 = 0, ptr2 = 0;
            rules = rules.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

            while (ptr1 < rules.Count && ptr2 < rules.Count)
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

        private async Task RandomWait()
        {
            var random = new Random();
            int waitTimeMS = random.Next(random.Next(0, nodeCount * 100), 1000 * nodeCount);

            await Task.Delay(waitTimeMS, Token).ConfigureAwait(false);
        }
    }
}