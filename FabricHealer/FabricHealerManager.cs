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

namespace FabricHealer
{
    public class FabricHealerManager : IDisposable
    {
        private static FabricHealerManager singleton = null;
        private bool disposedValue;
        private readonly StatelessServiceContext serviceContext;
        private readonly FabricClient fabricClient;
        private readonly RepairTaskManager repairTaskManager;
        private readonly RepairTaskEngine repairTaskEngine;
        private readonly Uri systemAppUri = new Uri("fabric:/System");
        private readonly Uri repairManagerServiceUri = new Uri("fabric:/System/RepairManagerService");
        private readonly FabricHealthReporter healthReporter;
        internal static TelemetryUtilities TelemetryUtilities;

        public static Logger RepairLogger
        {
            get; set;
        }

        public static ConfigSettings ConfigSettings
        {
            get; set;
        }

        // CancellationToken from FabricHealer.RunAsync. See ctor.
        public CancellationToken Token
        {
            get; set;
        }

        private FabricHealerManager(
            StatelessServiceContext context,
            CancellationToken token)
        {
            serviceContext = context;
            fabricClient = new FabricClient(FabricClientRole.Admin);
            Token = token;
            serviceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent += CodePackageActivationContext_ConfigurationPackageModifiedEvent;
            ConfigSettings = new ConfigSettings(context);
            TelemetryUtilities = new TelemetryUtilities(fabricClient, context);
            repairTaskEngine = new RepairTaskEngine(fabricClient);
            repairTaskManager = new RepairTaskManager(fabricClient, serviceContext, Token);
            RepairLogger = new Logger("FabricHealer")
            {
                EnableVerboseLogging = ConfigSettings.EnableVerboseLogging,
            };

            // Local Logger setup.
            string logFolderBasePath;
            string localLogPath = ConfigSettings.LocalLogPathParameter;

            if (!string.IsNullOrEmpty(localLogPath))
            {
                logFolderBasePath = localLogPath;
            }
            else
            {
                string logFolderBase = Path.Combine($@"{Environment.CurrentDirectory}", "fabrichealer_logs");
                logFolderBasePath = logFolderBase;
            }

            RepairLogger.LogFolderBasePath = logFolderBasePath;
            healthReporter = new FabricHealthReporter(fabricClient);
        }

        /// <summary>
        /// This is the static singleton instance of FabricHealerManager type. FabricHealerManager does not support
        /// multiple instantiations. It does not provide a public constructor.
        /// </summary>
        /// <param name="context">StatefulService context instance.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The singleton instance of FabricHealerManager.</returns>
        public static FabricHealerManager Singleton(
            StatelessServiceContext context,
            CancellationToken token)
        {
            if (singleton == null)
            {
                singleton = new FabricHealerManager(context ?? throw new ArgumentException("context can't be null"), token);
            }

            return singleton;
        }

        /// <summary>
        /// Checks if repair manager is enabled in the cluster or not
        /// </summary>
        /// <param name="serviceNameFabricUri"></param>
        /// <param name="cancellationToken">cancellation token to stop the async operation</param>
        /// <returns>true if repair manager application is present in cluster, otherwise false</returns>
        public async Task<bool> CheckRepairManagerDeploymentStatusAsync(
            Uri serviceNameFabricUri,
            CancellationToken cancellationToken)
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
                healthReport.Code = FabricObserverErrorWarningCodes.Ok;
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
        public static string GetSettingParameterValue(
            StatelessServiceContext context,
            string sectionName,
            string parameterName,
            string defaultValue = null)
        {
            if (string.IsNullOrEmpty(sectionName) || string.IsNullOrEmpty(parameterName))
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
                    return !string.IsNullOrEmpty(defaultValue) ? defaultValue : null;
                }

                if (serviceConfiguration.Settings.Sections[sectionName].Parameters.All(param => param.Name != parameterName))
                {
                    return !string.IsNullOrEmpty(defaultValue) ? defaultValue : null;
                }

                string setting = serviceConfiguration.Settings.Sections[sectionName].Parameters[parameterName]?.Value;

                if (string.IsNullOrEmpty(setting) && defaultValue != null)
                {
                    return defaultValue;
                }

                return setting;
            }
            catch (Exception e) when (
                    e is ArgumentException ||
                    e is KeyNotFoundException)
            {
            }

            return null;
        }

        // This function starts the detection workflow, which involves querying event store for 
        // Warning/Error heath events, looking for well-known FabricObserver error codes with supported
        // repair actions, scheduling and executing related repair tasks.
        public async Task StartAsync()
        {
            if (!ConfigSettings.EnableAutoMitigation
                ||!await CheckRepairManagerDeploymentStatusAsync(
                        repairManagerServiceUri,
                        Token).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                RepairLogger.LogInfo("Starting FabricHealer Health Detection loop.");

                // First, let's clean up any orphan non-node level FabricHealer repair tasks left pending 
                // when the FabricHealer process is killed or otherwise ungracefully closed.
                // This call will return quickly if FH was gracefully closed as there will be
                // no outstanding repair tasks left orphaned.
                await CancelOrResumeAllRunningFHRepairsAsync().ConfigureAwait(false);

                // Run until RunAsync token is canceled.
                while (!Token.IsCancellationRequested)
                {
                        if (!ConfigSettings.EnableAutoMitigation)
                        {
                            break;
                        }

                        if (!await MonitorRepairableHealthEventsAsync().ConfigureAwait(false))
                        {
                            continue;
                        }

                        if (ConfigSettings.ExecutionLoopSleepSeconds > 0)
                        {
                            RepairLogger.LogInfo(
                                $"Sleeping for {ConfigSettings.ExecutionLoopSleepSeconds} seconds before running again.");

                            await Task.Delay(TimeSpan.FromSeconds(ConfigSettings.ExecutionLoopSleepSeconds), Token).ConfigureAwait(false);
                        }
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
            catch (Exception e) when (
                    e is OperationCanceledException ||
                    e is TaskCanceledException ||
                    e is TimeoutException)
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
                var message = $"Unhanded Exception in FabricHealerManager:{Environment.NewLine}" +
                              $"Error info:{Environment.NewLine}{e}";

                RepairLogger.LogError(message);

                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Warning,
                        "FabricHealer",
                        message,
                        Token).ConfigureAwait(false);

                // ETW.
                if (ConfigSettings.EtwEnabled)
                {
                    Logger.EtwLogger?.Write(
                        RepairConstants.EventSourceEventName,
                        new
                        {
                            HealthState = "Warning",
                            Node = serviceContext.NodeContext.NodeName,
                            Source = "FabricHealer.FabricHealerManager",
                            Value = message,
                        });
                }

                // Don't swallow the exception.
                // Take down FH process. Fix the bugs.
                throw;
            }
        }

        /// <summary>
        /// Cancels all FabricHealer repair tasks currently in flight (unless in Restoring state).
        /// OR Resumes fabric node-level repairs that were abandoned due to FH going down while they were processing.
        /// </summary>
        /// <returns>A Task.</returns>
        public async Task CancelOrResumeAllRunningFHRepairsAsync()
        {
            var repairTaskEngine = new RepairTaskEngine(fabricClient);

            try
            {
                var currentFHRepairTasksInProgress =
                        await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                            () =>
                                repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(
                                RepairTaskEngine.FabricHealerExecutorName,
                                Token),
                            Token).ConfigureAwait(false);

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

                    if (string.IsNullOrEmpty(executorData))
                    {
                        continue;
                    }

                    if (!SerializationUtility.TryDeserialize(executorData, out RepairExecutorData repairExecutorData))
                    {
                        continue;
                    }

                    // Don't do anything if the orphaned repair was for a different node than this one:
                    // FH runs on all nodes, so don't cancel repairs for any other node than your own.
                    if (repairExecutorData.NodeName != serviceContext.NodeContext.NodeName)
                    {
                        continue;
                    }

                    // Cancel existing repair. We may need to create a new one for abandoned fabric node repairs.
                    await FabricRepairTasks.CancelRepairTaskAsync(repair, fabricClient).ConfigureAwait(false);

                    // There is no need to resume simple repairs that do not require multiple repair steps (e.g., service code package restarts).
                    if (repairExecutorData.RepairPolicy?.TargetType == RepairTargetType.Application
                        || repairExecutorData.RepairPolicy?.TargetType == RepairTargetType.Replica)
                    {
                        continue;
                    }

                    // Wait...
                    await Task.Delay(3000).ConfigureAwait(false);

                    /* Node-level repairs */

                    string errorCode = repairExecutorData.FOErrorCode;
                    
                    if (string.IsNullOrEmpty(errorCode))
                    {
                        return;
                    }

                    if (FabricObserverErrorWarningCodes.GetErrorWarningNameFromCode(errorCode).Contains("Disk"))
                    {
                        return;
                    }

                    List<string> repairRules;

                    // System warnings/errors from FO can be Node level. FH will restart the node hosting the troubled SF system service if specified in related logic rules.
                    if (repairExecutorData.RepairPolicy.RepairId.Contains("System"))
                    {
                        repairRules = GetRepairRulesFromConfiguration(RepairConstants.SystemAppRepairPolicySectionName);
                    }
                    else
                    {
                        repairRules = GetRepairRulesFromConfiguration(RepairConstants.FabricNodeRepairPolicySectionName);
                    }

                    TelemetryData foHealthData = new TelemetryData
                    {
                        NodeName = repairExecutorData.NodeName,
                        Code = errorCode,
                    };

                    _ = await repairTaskManager.InitializeGuanAndRunQuery(
                                foHealthData,
                                repairRules,
                                repairExecutorData);
                }
            }
            catch (Exception e) when (
                    e is FabricException ||
                    e is OperationCanceledException ||
                    e is TimeoutException)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"CancelOrResumeAllRunningFHRepairsAsync",
                            $"Could not cancel or resume repair tasks. Failed with:{Environment.NewLine}{e}",
                            Token).ConfigureAwait(false);
            }
        }

        private async void CodePackageActivationContext_ConfigurationPackageModifiedEvent(
                object sender,
                PackageModifiedEventArgs<ConfigurationPackage> e)
        {
            ConfigSettings.UpdateConfigSettings(e.NewPackage.Settings);

            if (ConfigSettings.EnableAutoMitigation)
            {
                await StartAsync().ConfigureAwait(false);
            }
        }

        // app/service/replica, Fabric node, and VM repair is implemented.
        // Note that for replica issues, FO does not support replica monitoring and as such,
        // it does not emit specific error codes that FH recognizes. The impl below therefore has no
        // dependency on FO health data.

        /* TODO...

            Symptom                                                 Mitigation 
            ------------------------------------------------------  ---------------------------------------------------
            Application Unresponsive (Memory leak)	                Restart the related Service
            Application Unresponsive (Socket Leak)	                Restart the related Service
            Application Unresponsive (High CPU)	                    Restart the related Service
            Exhaust Disk space: FabricDCA unable upload	            Clean the  DCA log folder
            Expired Certificate [TP Scenario]	                    Modify the cluster manifest AEPCC to true (we already have an automation script for this scenario)
            Node crash due to lease issue 	                        Restart the neighboring VM
            Node Crash due to slow network issue	                Restart the VM
            Node failed open due to dockerd connectivity            Reimage the VM
            System Service in quorum loss	                        Repair the partition/ Restart the VM
            Node stuck in disabling state due to MR [safety check]	Address safety issue through automation
            [MR Scenario] Node in down state: MR unable 
            to send the Remove-ServiceFabricNodeState in time	    Remove-ServiceFabricNodeState
            Unused container fill the disk space	                Call docker prune cmd 
            Primary replica for system service in IB state forever	Restart the primary partition 

        */
        private async Task<bool> MonitorRepairableHealthEventsAsync()
        {
            try
            {
                var clusterHealth = await fabricClient.HealthManager.GetClusterHealthAsync(ConfigSettings.AsyncTimeout, Token).ConfigureAwait(false);

                // Node-level safe restarts, should that be the specified repair for a node repair
                // (like for a Fabric process), must not take place in clusters with less than 3 nodes to guarantee quorum.
                if (clusterHealth.AggregatedHealthState == HealthState.Ok)
                {
                    return true;
                }

                // Check cluster upgrade status. If the cluster is upgrading to a new version (or rolling back)
                // then do not attempt repairs.
                int udInClusterUpgrade = await UpgradeChecker.GetUdsWhereFabricUpgradeInProgressAsync(
                                                    fabricClient,
                                                    Token).ConfigureAwait(false);

                if (udInClusterUpgrade > -1 && udInClusterUpgrade < int.MaxValue)
                {
                    string telemetryDescription =
                            $"Cluster is currently upgrading in UD {udInClusterUpgrade}. " +
                            $"Will not schedule or execute repairs at this time.";

                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                           LogLevel.Info,
                           $"MonitorRepairableHealthEventsAsync::ClusterUpgradeDetected",
                           telemetryDescription,
                           Token).ConfigureAwait(false);

                    return true;
                }

                // Check to see if an Azure tenant update is in progress. Do not conduct repairs if so.
                if (await UpgradeChecker.IsAzureTenantUpdateInProgress(
                                fabricClient,
                                serviceContext.NodeContext.NodeType,
                                Token).ConfigureAwait(false))
                {
                    return true;
                }

                var unhealthyEvaluations = clusterHealth.UnhealthyEvaluations;

                foreach (var evaluation in unhealthyEvaluations)
                {
                    Token.ThrowIfCancellationRequested();

                    string kind = Enum.GetName(typeof(HealthEvaluationKind), evaluation.Kind);

                    if (kind.Contains("Node"))
                    {
                        if (!ConfigSettings.EnableVmRepair)
                        {
                            continue;
                        }

                        try
                        {
                            await ProcessNodeHealthAsync(clusterHealth.NodeHealthStates).ConfigureAwait(false);
                        }
                        catch (Exception e) when
                        (e is FabricException ||
                         e is OperationCanceledException ||
                         e is TimeoutException)
                        {
#if DEBUG
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                $"MonitorRepairableHealthEventsAsync::HandledException",
                                $"Failure in MonitorRepairableHealthEventAsync::Node:{Environment.NewLine}{e}",
                                Token);
#endif
                        }
                    }
                    else if (kind.Contains("Application"))
                    {
                        if (!ConfigSettings.EnableAppRepair && !ConfigSettings.EnableSystemAppRepair)
                        {
                            continue;
                        }

                        try
                        {
                            await ProcessApplicationHealthAsync(clusterHealth.ApplicationHealthStates).ConfigureAwait(false);
                        }
                        catch (Exception e) when
                        (e is FabricException ||
                         e is OperationCanceledException ||
                         e is TimeoutException)
                        {
#if DEBUG
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                $"MonitorRepairableHealthEventsAsync::HandledException",
                                $"Failure in MonitorRepairableHealthEventAsync::Application:{Environment.NewLine}{e}",
                                Token);
#endif
                        }
                    }
                    // FYI: FH currently only supports the case where a replica is stuck. FO does not emit ReplicaHealthReports.
                    else if (kind.Contains("Replica"))
                    {
                        if (!ConfigSettings.EnableReplicaRepair)
                        {
                            continue;
                        }

                        try
                        {
                            await ProcessReplicaHealthAsync(evaluation).ConfigureAwait(false);
                        }
                        catch (Exception e) when
                        (e is FabricException ||
                         e is TimeoutException ||
                         e is OperationCanceledException)
                        {
#if DEBUG
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                $"MonitorRepairableHealthEventsAsync::HandledException",
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
            catch (Exception e) when
                   (e is FabricException ||
                    e is OperationCanceledException ||
                    e is TimeoutException)
            {
                return false;
            }
            catch (Exception e)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    $"MonitorRepairableHealthEventsAsync::UnhandledException",
                    $"Failure in MonitorRepairableHealthEventAsync:{Environment.NewLine}{e}",
                    Token);

                RepairLogger.LogWarning($"Unhandled exception in MonitorRepairableHealthEventsAsync:{Environment.NewLine}{e}");

                // Fix the bug(s)..
                throw;
            }
        }

        private async Task ProcessApplicationHealthAsync(IList<ApplicationHealthState> appHealthStates)
        {
            var supportedAppHealthStates = appHealthStates.Where(a => a.AggregatedHealthState == HealthState.Warning || a.AggregatedHealthState == HealthState.Error);
            var nodeList = await fabricClient.QueryManager.GetNodeListAsync(null, ConfigSettings.AsyncTimeout, Token).ConfigureAwait(false);
            int nodeCount = nodeList.Count;

            foreach (var app in supportedAppHealthStates)
            {
                Token.ThrowIfCancellationRequested();

                var appHealth = await fabricClient.HealthManager.GetApplicationHealthAsync(app.ApplicationName).ConfigureAwait(false);
                var appName = app.ApplicationName;

                if (appName.OriginalString != "fabric:/System")
                {
                    var appUpgradeStatus =
                        await fabricClient.ApplicationManager.GetApplicationUpgradeProgressAsync(appName);

                    if (appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingBackInProgress
                        || appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingForwardInProgress
                        || appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingForwardPending)
                    {
                        var udInAppUpgrade = await UpgradeChecker.GetUdsWhereApplicationUpgradeInProgressAsync(
                            fabricClient,
                            Token,
                            appName);

                        string udText = string.Empty;

                        // -1 means no upgrade in progress for application
                        // int.MaxValue means an exception was thrown during upgrade check and you should
                        // check the logs for what went wrong, then fix the bug (if it's a bug you can fix).
                        if (udInAppUpgrade.Any(ud => ud > -1 && ud < int.MaxValue))
                        {
                            udText = $"in UD {udInAppUpgrade.First(ud => ud > -1 && ud < int.MaxValue)}";
                        }

                        string telemetryDescription =
                            $"{appName} is upgrading {udText}. " +
                            $"Will not attempt application repair at this time.";

                        await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                $"MonitorRepairableHealthEventsAsync::AppUpgradeDetected",
                                telemetryDescription,
                                Token).ConfigureAwait(false);

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

                    // Random wait to limit duplicate job creation from other FH instances.
                    // This is hacky and will be replaced at some point with a better approach. This is fine for Beta.
                    var random = new Random();
                    int waitTime = random.Next(nodeCount + 42);
                    await Task.Delay(TimeSpan.FromSeconds(waitTime), Token).ConfigureAwait(false);

                    if (string.IsNullOrEmpty(evt.HealthInformation.Description))
                    {
                        continue;
                    }   

                    if (!SerializationUtility.TryDeserialize(evt.HealthInformation.Description, out TelemetryData foHealthData))
                    {
                        continue;
                    }

                    if (!FabricObserverErrorWarningCodes.AppErrorCodesDictionary.ContainsKey(foHealthData.Code)
                        || foHealthData.Code == FabricObserverErrorWarningCodes.AppErrorNetworkEndpointUnreachable
                        || foHealthData.Code == FabricObserverErrorWarningCodes.AppWarningNetworkEndpointUnreachable)
                    {
                        // Network endpoint test failures have no general mitigation yet.
                        continue;
                    }

                    // Get configuration settings related to Application (service code package) repair.
                    List<string> repairRules;
                    string repairId;
                    string system = string.Empty;

                    if (app.ApplicationName.OriginalString == "fabric:/System")
                    {
                        // Node-level safe restarts, should that be the specified repair for a system service issue
                        // (like for a Fabric process), must not take place in clusters with less than 3 nodes to guarantee quorum..
                        if (nodeCount < 3)
                        {
                            continue;
                        }

                        // Block attempts to schedule node-level or system service restart repairs if one is already executing in the cluster.
                        var fhRepairTasks = await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(
                                                    RepairTaskEngine.FabricHealerExecutorName,
                                                    Token).ConfigureAwait(false);

                        if (fhRepairTasks.Count > 0)
                        {
                            foreach (var repair in fhRepairTasks)
                            {
                                var executorData =  SerializationUtility.TryDeserialize(repair.ExecutorData, out RepairExecutorData exData) ? exData : null;
                              
                                if (executorData?.RepairPolicy.RepairAction == RepairActionType.RestartFabricNode 
                                    || executorData?.RepairPolicy.RepairAction == RepairActionType.RestartProcess)
                                {
                                    string message = $"A Service Fabric System service repair ({repair.TaskId}) is already in progress in the cluster. Will not attempt repair at this time.";

                                    TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                LogLevel.Info,
                                                $"ProcessApplicationHealth::System::{repair.TaskId}",
                                                message,
                                                Token).GetAwaiter().GetResult();

                                    return;
                                }
                            }
                        }

                        repairRules = GetRepairRulesFromFOCode(foHealthData.Code, "fabric:/System");
                        
                        if (repairRules == null || repairRules?.Count == 0)
                        {
                            continue;
                        }

                        repairId = $"{foHealthData.NodeName}_{foHealthData.SystemServiceProcessName}_{foHealthData.Code}";
                        system = "System ";

                        var currentRepairs = await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token).ConfigureAwait(false);

                        // Is a repair for the target app service instance already happening in the cluster? There can be multiple Warnings emitted by FO for a single app at the same time.
                        if (currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(foHealthData.SystemServiceProcessName)))
                        {
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                       LogLevel.Info,
                                       $"MonitorRepairableHealthEventsAsync::{foHealthData.SystemServiceProcessName}",
                                       $"There is already a repair in progress for Fabric system service {foHealthData.SystemServiceProcessName}",
                                       Token).ConfigureAwait(false);

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
                        string repairIdPart = $"";
                        var currentRepairs = await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token).ConfigureAwait(false);

                        // Is a repair for the target app service instance already happening in the cluster? There can be multiple Warnings emitted by FO for a single app at the same time.
                        if (currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(serviceProcessName)))
                        {
                            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                       LogLevel.Info,
                                       $"MonitorRepairableHealthEventsAsync::{foHealthData.ServiceName}",
                                       $"{appName} already has a repair in progress for service {foHealthData.ServiceName}",
                                       Token).ConfigureAwait(false);

                            continue;
                        }

                        // This is the way each FH repair is ID'd. This data is stored in the related Repair Task's ExecutorData property.
                        repairId = $"{foHealthData.NodeName}_{serviceProcessName}_{foHealthData.Metric?.Replace(" ", string.Empty)}";
                        
                        // Repair already in progress on target node (so, the job has already been taken by another FH instance at this point)?
                        if (currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(repairId)))
                        {
                            continue;
                        }
                    }

                    foHealthData.RepairId = repairId;
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
                            $"{system}Application repair policy is enabled. {repairRules.Count} Logic rules found for {system}Application-level repair.",
                            Token).ConfigureAwait(false);

                    // Schedule repair task.
                    await repairTaskManager.StartRepairWorkflowAsync(foHealthData, repairRules, Token).ConfigureAwait(false);
                }
            }
        }

        // As far as FabricObserver(FO) is concerned, a node is a VM, so FO only creates NodeHealthReports when a VM level issue
        // is detected. So, FO does not monitor Fabric node health, but will put a node in Error or Warning if the underlying 
        // VM is using too much of some monitored machine resource..).
        private async Task ProcessNodeHealthAsync(IList<NodeHealthState> nodeHealthStates)
        {
            var supportedNodeHealthStates = nodeHealthStates.Where(a => a.AggregatedHealthState == HealthState.Warning || a.AggregatedHealthState == HealthState.Error);

            foreach (var node in supportedNodeHealthStates)
            {
                Token.ThrowIfCancellationRequested();

                // Get target node. Make sure it still exists in cluster..
                var nodeList = await fabricClient.QueryManager.GetNodeListAsync(
                    node.NodeName,
                    ConfigSettings.AsyncTimeout, 
                    Token).ConfigureAwait(false);

                // No node-level repairs in dev clusters..
                if (nodeList.Count < 3)
                {
                    continue;
                }

                var targetNode = nodeList[0];

                // Check to see if a VM-level repair is already scheduled or in flight for the target node.
                var currentFHVMRepairTasksInProgress =
                            await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(
                                $"fabric:/System/InfrastructureService/{targetNode.NodeType}",
                                Token).ConfigureAwait(false);

                // FH creates VM repair tasks with custom Description that always contains node name. Of course,
                // same is true for TaskId. Doesn't matter which one we check here..
                if (currentFHVMRepairTasksInProgress.Count > 0 
                    && currentFHVMRepairTasksInProgress.Any(r => r.Description.Contains(targetNode.NodeName) || r.TaskId.Contains(targetNode.NodeName)))
                {
                    continue;
                }
            
                var nodeHealth = await fabricClient.HealthManager.GetNodeHealthAsync(node.NodeName).ConfigureAwait(false);
                var observerHealthEvents = nodeHealth.HealthEvents.Where(
                                            s => s.HealthInformation.SourceId.ToLower().Contains("observer")
                                              && (s.HealthInformation.HealthState == HealthState.Warning 
                                                  || s.HealthInformation.HealthState == HealthState.Error));
                
                foreach (var evt in observerHealthEvents)
                {
                    Token.ThrowIfCancellationRequested();

                    // Random wait to limit duplicate job creation attempts from other FH instances.
                    // This is hacky and will be replaced at some point with a better approach. This is fine for Beta.
                    var random = new Random();
                    int waitTime = random.Next(nodeList.Count + 42);
                    await Task.Delay(TimeSpan.FromSeconds(waitTime), Token).ConfigureAwait(false);

                    if (string.IsNullOrEmpty(evt.HealthInformation.Description))
                    {
                        continue;
                    }

                    if (!SerializationUtility.TryDeserialize(evt.HealthInformation.Description, out TelemetryData foHealthData))
                    {
                        continue;
                    }

                    // Supported Error code from FO? If not, then get next event..
                    if (!FabricObserverErrorWarningCodes.NodeErrorCodesDictionary.ContainsKey(foHealthData.Code))
                    {
                        continue;
                    }

                    string errorWarningName = FabricObserverErrorWarningCodes.GetErrorWarningNameFromCode(foHealthData.Code);

                    // Don't try any VM-level repairs - except for Disk repair - if the target VM is the same one where this instance of FH is running.
                    // Another FH instance on a different VM will run repair.
                    if (foHealthData.NodeName == serviceContext.NodeContext.NodeName)
                    {
                        // Disk file maintenance (deletion) can only take place on the same node where the disk space issue was detected by FO..
                        if (!errorWarningName.Contains("Disk"))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        // FH can't clean directories on other VMs in the cluster..
                        if (errorWarningName.Contains("Disk"))
                        {
                            continue;
                        }
                    }

                    if (string.IsNullOrEmpty(errorWarningName))
                    {
                        continue;
                    }

                    // Get configuration settings related to supported Node repair.
                    var repairRules = GetRepairRulesFromFOCode(foHealthData.Code);

                    if (repairRules == null || repairRules?.Count == 0)
                    {
                        continue;
                    }

                    string Id = $"VM_Repair_{foHealthData.Code}{foHealthData.NodeName}";
                    foHealthData.RepairId = Id;

                    string errOrWarn = "Error";

                    if (evt.HealthInformation.HealthState == HealthState.Warning)
                    {
                        errOrWarn = "Warning";
                    }

                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"MonitorRepairableHealthEventsAsync:{Id}",
                            $"Detected VM hosting {foHealthData.NodeName} is in {errOrWarn}. " +
                            $"{errOrWarn} Code: {foHealthData.Code}({FabricObserverErrorWarningCodes.GetErrorWarningNameFromCode(foHealthData.Code)}){Environment.NewLine}" +
                            $"VM repair policy is enabled. {repairRules.Count} Logic rules found for VM-level repair.",
                            Token).ConfigureAwait(false);

                    // Schedule repair task. This will only schedule once if there is an existing
                    // repair job that is not completed yet.
                    await repairTaskManager.StartRepairWorkflowAsync(foHealthData, repairRules, Token).ConfigureAwait(false);
                }
            }
        }

        // This is an example of a repair for a non-FO-originating health event. This function needs some work, but you get the basic idea here.
        // This has not been tested thoroughly..
        private async Task ProcessReplicaHealthAsync(HealthEvaluation evaluation)
        {
            if (evaluation.Kind != HealthEvaluationKind.Replica)
            {
                return;
            }

            var nodeList = await fabricClient.QueryManager.GetNodeListAsync(null, ConfigSettings.AsyncTimeout, Token).ConfigureAwait(false);
            int nodeCount = nodeList.Count;
            var repUnhealthyEvaluations = ((ReplicaHealthEvaluation)evaluation).UnhealthyEvaluations;

            foreach (var healthEvaluation in repUnhealthyEvaluations)
            {
                var eval = (ReplicaHealthEvaluation)healthEvaluation;
                var healthEvent = eval.UnhealthyEvaluations.Cast<EventHealthEvaluation>().FirstOrDefault();

                Token.ThrowIfCancellationRequested();

                // Random wait to limit duplicate job creation from other FH instances.
                // This is hacky and will be replaced at some point with a better approach. This is fine for Beta.
                var random = new Random();
                int waitTime = random.Next(nodeCount + 42);
                await Task.Delay(TimeSpan.FromSeconds(waitTime)).ConfigureAwait(false);

                var service = await fabricClient.QueryManager.GetServiceNameAsync(
                    eval.PartitionId,
                    ConfigSettings.AsyncTimeout,
                    Token).ConfigureAwait(false);

                /*  Example of repairable problem at Replica level, as health event:

                    [SourceId] ='System.RAP' reported Warning/Error for property...
                    [Property] = 'IStatefulServiceReplica.ChangeRole(N)Duration'.
                    [Description] = The api IStatefulServiceReplica.ChangeRole(N) on node [NodeName] is stuck. 

                    Start Time (UTC): 2020-04-26 19:22:55.492. 
                */

                if (string.IsNullOrEmpty(healthEvent.UnhealthyEvent.HealthInformation.Description))
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
                                    Token).ConfigureAwait(false);

                var replicaList = await fabricClient.QueryManager.GetReplicaListAsync(
                                            eval.PartitionId,
                                            eval.ReplicaOrInstanceId,
                                            ConfigSettings.AsyncTimeout,
                                            Token).ConfigureAwait(false);
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
                    ReplicaId = eval.ReplicaOrInstanceId.ToString(),
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
                var currentRepairs = await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, Token).ConfigureAwait(false);
                
                if (currentRepairs.Count > 0 && currentRepairs.Any(r => r.ExecutorData.Contains(repairId)))
                {
                    continue;
                }

                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        $"MonitorRepairableHealthEventsAsync:Replica_{eval.ReplicaOrInstanceId}_{errOrWarn}",
                        $"Detected Replica {eval.ReplicaOrInstanceId} on Partition {eval.PartitionId} is in {errOrWarn}.{Environment.NewLine}" +
                        $"Replica repair policy is enabled. {repairRules.Count} Logic rules found for unhealthy Replica repair.",
                        Token).ConfigureAwait(false);

                await repairTaskManager.StartRepairWorkflowAsync(foHealthData, repairRules, Token).ConfigureAwait(false);
            }
        }
        
        private List<string> GetRepairRulesFromFOCode(string foErrorCode, string app = null)
        {
            if (!FabricObserverErrorWarningCodes.AppErrorCodesDictionary.ContainsKey(foErrorCode)
                && !FabricObserverErrorWarningCodes.NodeErrorCodesDictionary.ContainsKey(foErrorCode))
            {
                return null;
            }

            string repairPolicySectionName;

            switch (foErrorCode)
            {
                // App level.
                case FabricObserverErrorWarningCodes.AppErrorCpuPercent:
                case FabricObserverErrorWarningCodes.AppErrorMemoryMB:
                case FabricObserverErrorWarningCodes.AppErrorMemoryPercent:
                case FabricObserverErrorWarningCodes.AppErrorTooManyActiveEphemeralPorts:
                case FabricObserverErrorWarningCodes.AppErrorTooManyActiveTcpPorts:
                case FabricObserverErrorWarningCodes.AppErrorTooManyOpenFileHandles:
                case FabricObserverErrorWarningCodes.AppWarningCpuPercent:
                case FabricObserverErrorWarningCodes.AppWarningMemoryMB:
                case FabricObserverErrorWarningCodes.AppWarningMemoryPercent:
                case FabricObserverErrorWarningCodes.AppWarningTooManyActiveEphemeralPorts:
                case FabricObserverErrorWarningCodes.AppWarningTooManyActiveTcpPorts:
                case FabricObserverErrorWarningCodes.AppWarningTooManyOpenFileHandles:


                    if (app == "fabric:/System")
                    {
                        repairPolicySectionName = RepairConstants.SystemAppRepairPolicySectionName;
                    }
                    else
                    {
                        repairPolicySectionName = RepairConstants.AppRepairPolicySectionName;
                    }
                    break;

                // Node level. (node = VM, not Fabric node)
                case FabricObserverErrorWarningCodes.NodeErrorCpuPercent:
                case FabricObserverErrorWarningCodes.NodeErrorMemoryMB:
                case FabricObserverErrorWarningCodes.NodeErrorMemoryPercent:
                case FabricObserverErrorWarningCodes.NodeErrorTooManyActiveEphemeralPorts:
                case FabricObserverErrorWarningCodes.NodeErrorTooManyActiveTcpPorts:
                case FabricObserverErrorWarningCodes.NodeErrorTotalOpenFileHandlesPercent:
                case FabricObserverErrorWarningCodes.NodeWarningCpuPercent:
                case FabricObserverErrorWarningCodes.NodeWarningMemoryMB:
                case FabricObserverErrorWarningCodes.NodeWarningMemoryPercent:
                case FabricObserverErrorWarningCodes.NodeWarningTooManyActiveEphemeralPorts:
                case FabricObserverErrorWarningCodes.NodeWarningTooManyActiveTcpPorts:
                case FabricObserverErrorWarningCodes.NodeWarningTotalOpenFileHandlesPercent:

                    repairPolicySectionName = RepairConstants.VmRepairPolicySectionName;
                    break;

                case FabricObserverErrorWarningCodes.NodeWarningDiskSpaceMB:
                case FabricObserverErrorWarningCodes.NodeErrorDiskSpaceMB:
                case FabricObserverErrorWarningCodes.NodeWarningDiskSpacePercent:
                case FabricObserverErrorWarningCodes.NodeErrorDiskSpacePercent:

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
                    RepairConstants.LogicRulesConfigurationFile,
                    null);

            var configPath = serviceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Path;
            var rulesFolderPath = Path.Combine(configPath, "Rules");
            var rulesFilePath = Path.Combine(rulesFolderPath, logicRulesConfigFileName);
            List<string> rules = File.ReadAllLines(rulesFilePath).ToList();
            List<string> repairRules = ParseRulesFile(rules);

            return repairRules;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    fabricClient?.Dispose();
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        private List<string> ParseRulesFile(List<string> rules)
        {
            var repairRules = new List<string>();
            int ptr1 = 0; int ptr2 = 0;
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
    }
}