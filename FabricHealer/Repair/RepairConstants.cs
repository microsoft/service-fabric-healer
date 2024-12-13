// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricHealer.Repair
{
    public static class RepairConstants
    {
        // Logic rules.
        public const string LogicRulesConfigurationFile = "LogicRulesConfigurationFile";
        public const string LogicRulesFolderName = "LogicRules";

        // Health event sourceId constants.
        public const string InfrastructureServiceType = "InfrastructureServiceType";

        // Telemetry Settings Parameters.
        public const string TelemetryProviderType = "TelemetryProvider";
        public const string LogAnalyticsLogTypeParameter = "LogAnalyticsLogType";
        public const string LogAnalyticsSharedKeyParameter = "LogAnalyticsSharedKey";
        public const string LogAnalyticsWorkspaceIdParameter = "LogAnalyticsWorkspaceId";
        public const string EventSourceProviderName = "FabricHealerETWProvider";
        public const string EventSourceEventName = "FabricHealerDataEvent";

        // RepairManager Settings Parameters.
        public const string RepairManagerConfigurationSectionName = "RepairManagerConfiguration";
        public const string EnableVerboseLoggingParameter = "EnableVerboseLogging";
        public const string EnableTelemetry = "EnableTelemetry";
        public const string EnableRollingServiceRestartsParameter = "EnableRollingServiceRestarts";
        public const string AppInsightsInstrumentationKeyParameter = "AppInsightsInstrumentationKey";
        public const string AppInsightsConnectionStringParameter = "AppInsightsConnectionString";
        public const string EnableETW = "EnableETW";
        public const string HealthCheckIntervalInSeconds = "HealthCheckIntervalInSeconds";
        public const string LocalLogPathParameter = "LocalLogPath";
        public const string AsyncOperationTimeout = "AsyncOperationTimeoutSeconds";
        public const string EnableFabricHealerOperationalTelemetry = "EnableOperationalTelemetry";
        public const string EnableLogicRuleTracing = "EnableLogicRuleTracing";
        public const string EnableCustomServiceInitializers = nameof(EnableCustomServiceInitializers);
        public const string EnableCustomRepairPredicateType = nameof(EnableCustomRepairPredicateType);

        // General Repair Settings Parameters.
        public const string EnableAutoMitigation = "EnableAutoMitigation";
        public const string MaxRepairsInTimeRange = "MaxRepairsInTimeRange";

        // RepairPolicy Settings Sections.
        public const string FabricNodeRepairPolicySectionName = "FabricNodeRepairPolicy";
        public const string ReplicaRepairPolicySectionName = "ReplicaRepairPolicy";
        public const string AppRepairPolicySectionName = "AppRepairPolicy";
        public const string DiskRepairPolicySectionName = "DiskRepairPolicy";
        public const string SystemServiceRepairPolicySectionName = "SystemServiceRepairPolicy";
        public const string MachineRepairPolicySectionName = "MachineRepairPolicy";

        // RepairPolicy
        public const string Enabled = "Enabled";

        // Mitigate Argument names.
        public const string AppName = "AppName";
        public const string ErrorCode = "ErrorCode";
        public const string EntityType = "EntityType";
        public const string HealthState = "HealthState";
        public const string MetricName = "MetricName";
        public const string MetricValue = "MetricValue";
        public const string NodeName = "NodeName";
        public const string NodeType = "NodeType";
        public const string ObserverName = "ObserverName";
        public const string OS = "OS";
        public const string PartitionId = "PartitionId";
        public const string ReplicaOrInstanceId = "ReplicaOrInstanceId";
        public const string ReplicaRole = "ReplicaRole";
        public const string ProcessId = "ProcessId";
        public const string ProcessName = "ProcessName";
        public const string ProcessStartTime = "ProcessStartTime";
        public const string Property = "Property";
        public const string ServiceName = "ServiceName";
        public const string ServiceKind = "ServiceKind";
        public const string Source = "Source";

        // Repair Actions.
        public const string DeactivateFabricNode = "DeactivateFabricNode";
        public const string DeleteFiles = "DeleteFiles";
        public const string RestartCodePackage = "RestartCodePackage";
        public const string RestartFabricNode = "RestartFabricNode";
        public const string RestartFabricSystemProcess = "RestartFabricSystemProcess";
        public const string RestartReplica = "RestartReplica";
        public const string ScheduleMachineRepair = "ScheduleMachineRepair";
        public const string ScheduleDiskReimage = "ScheduleDiskReimage";

        // Infra repair names (RM "commands").
        public const string SystemReboot = "System.Reboot";
        public const string SystemReimageOS = "System.ReimageOS";
        public const string SystemFullReimage = "System.FullReimage";
        public const string SystemHostReboot = "System.Azure.HostReboot";
        public const string SystemHostRepaveData = "System.Azure.HostRepaveData";

        // Helper Predicates.
        public const string CheckInsideRunInterval = "CheckInsideRunInterval";
        public const string CheckInsideScheduleInterval = "CheckInsideScheduleInterval";
        public const string CheckInsideNodeProbationPeriod = "CheckInsideNodeProbationPeriod";
        public const string CheckOutstandingRepairs = "CheckOutstandingRepairs";
        public const string CheckFolderSize = "CheckFolderSize";
        public const string CheckInsideHealthStateMinDuration = "CheckInsideHealthStateMinDuration";
        public const string GetHealthEventHistory = "GetHealthEventHistory";
        public const string GetRepairHistory = "GetRepairHistory";
        public const string LogInfo = "LogInfo";
        public const string LogWarning = "LogWarning";
        public const string LogError = "LogError";
        public const string LogRule = "LogRule";

        // Metric names.
        public const string ActiveTcpPorts = "ActiveTcpPorts";
        public const string Certificate = "Certificate";
        public const string Cpu = "Cpu";
        public const string CpuPercent = "CpuPercent";
        public const string DiskAverageQueueLength = "DiskAverageQueueLength";
        public const string DiskSpaceMB = "DiskSpaceMB";
        public const string FolderSizeMB = "FolderSizeMB";
        public const string DiskSpacePercent = "DiskSpacePercent";
        public const string EphemeralPorts = "EphemeralPorts";
        public const string EphemeralPortsPercent = "EphemeralPortsPercent";
        public const string EndpointUnreachable = "EndpointUnreachable";
        public const string FirewallRules = "FirewallRules";
        public const string MemoryMB = "MemoryMB";
        public const string MemoryPercent = "MemoryPercent";
        public const string Handles = "Handles";
        public const string HandlesPercent = "HandlesPercent";
        public const string Threads = "Threads";

        // Supported FabricObserver Observer Names
        public const string AppObserver = "AppObserver";
        public const string ContainerObserver = "ContainerObserver";
        public const string DiskObserver = "DiskObserver";
        public const string FabricSystemObserver = "FabricSystemObserver";
        public const string NodeObserver = "NodeObserver";

        // General
        public const string SystemAppName = "fabric:/System";
        public const string InfrastructureService = "InfrastructureService";
        public const string InfrastructureServiceName = "fabric:/System/InfrastructureService";
        public const string FabricHealerAppName = "fabric:/FabricHealer";
        public const string RepairManagerAppName = "fabric:/System/RepairManagerService";
        public const string RepairData = "RepairData";
        public const string RepairPolicy = "RepairPolicy";
        public const string FabricHealer = "FabricHealer";
        public const string FabricHealerDataEvent = "FabricHealerDataEvent";
        public const string FabricHealerETWProvider = "FabricHealerETWProvider";
        public const string FHTaskIdPrefix = "FH";
        public const string InfraTaskIdPrefix = "FH_Infra";
        public const string AzureTaskIdPrefix = "Azure";
        public const string FabricHealerStartAction = "FabricHealer.Start";
        public const string FabricHealerStopAction = "FabricHealer.Stop";

        // Overridable machine repair helper predicate arguments.
        public const string ScheduleInterval = "ScheduleInterval";
        public const string MaxOutstandingRepairs = "MaxOutstandingRepairs";
        public const string NodeProbationPeriod = "NodeProbationPeriod";
        public const string MinHealthStateDuration = "MinHealthStateDuration";

        // Octokit Github Version Check
        public const string CheckGithubVersion = "CheckGithubVersion";
    }
}