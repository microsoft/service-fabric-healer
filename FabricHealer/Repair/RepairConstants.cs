// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricHealer.Repair
{
    public static class RepairConstants
    {
        // Queue Constants
        public const int QueueRetries = 3;

        // Time Constants
        public const int QueueVisibilityTimeInMin = 60;
        public const int TaskDelayTimeInMin = 3;
        public const int QueueRetryTimeInSec = 3;
        public const int QueueRetryCount = 5;

        // Logic rules file parameter.
        public const string LogicRulesConfigurationFile = "LogicRulesConfigurationFile";

        // Health event sourceId constants.
        public const string FabricObserverSourceId = "FabricObserver";
        public const string InfrastructureServiceSourceId = "System.InfrastructureService";
        public const string RepairPolicyEngineServiceSourceId = "RepairPolicyEngineService";
        public const string MonitoringHealthProperty = "MonitoringHealth";
        public const string InfrastructureServiceType = "InfrastructureServiceType";

        // Telemetry Settings Parameters.
        public const string TelemetryProviderType = "TelemetryProvider";
        public const string LogAnalyticsLogTypeParameter = "LogAnalyticsLogType";
        public const string LogAnalyticsSharedKeyParameter = "LogAnalyticsSharedKey";
        public const string LogAnalyticsWorkspaceIdParameter = "LogAnalyticsWorkspaceId";
        public const string EventSourceEventName = "FabricHealerDataEvent";

        // RepairManager Settings Parameters.
        public const string RepairManagerConfigurationSectionName = "RepairManagerConfiguration";
        public const string EnableVerboseLoggingParameter = "EnableVerboseLogging";
        public const string ShutdownGracePeriodInSeconds = "ShutdownGracePeriodInSeconds";
        public const string AppInsightsTelemetryEnabled = "EnableTelemetryProvider";
        public const string AppInsightsInstrumentationKeyParameter = "AppInsightsInstrumentationKey";
        public const string FabricHealerpublicTelemetryEnabled = "FabricHealerpublicTelemetryEnabled";
        public const string EnableEventSourceProvider = "EnableEventSourceProvider";
        public const string EventSourceProviderName = "EventSourceProviderName";
        public const string HealthCheckLoopSleepTimeSeconds = "HealthCheckLoopSleepTimeSeconds";
        public const string LocalLogPathParameter = "LocalLogPath";
        public const string AsyncOperationTimeout = "AsyncOperationTimeoutSeconds";

        // General Repair Settings Parameters.
        public const string EnableAutoMitigation = "EnableAutoMitigation";

        // RepairPolicy Settings Sections.
        public const string FabricNodeRepairPolicySectionName = "FabricNodeRepairPolicy";
        public const string ReplicaRepairPolicySectionName = "ReplicaRepairPolicy";
        public const string AppRepairPolicySectionName = "AppRepairPolicy";
        public const string DiskRepairPolicySectionName = "DiskRepairPolicy";
        public const string SystemAppRepairPolicySectionName = "SystemAppRepairPolicy";
        public const string VmRepairPolicySectionName = "VMRepairPolicy";

        // RepairPolicy Settings Parameters.
        public const string ActionParameter = "RepairAction";
        public const string Enabled = "Enabled";

        public const string AppName = "AppName";
        public const string ServiceName = "ServiceName";
        public const string NodeName = "NodeName";
        public const string NodeType = "NodeType";
        public const string PartitionId = "PartitionId";
        public const string ReplicaOrInstanceId = "ReplicaOrInstanceId";
        public const string TargetType = "TargetType";
        public const string CycleTimeDistributionType = "CycleTimeDistributionType";
        public const string RunInterval = "RunInterval";
        public const string FOErrorCode = "FOErrorCode";
        public const string MetricName = "MetricName";
        public const string MetricValue = "MetricValue";

        // Repair Actions.
        public const string DeleteFiles = "DeleteFiles";
        public const string RestartCodePackage = "RestartCodePackage";
        public const string RestartFabricNode = "RestartFabricNode";
        public const string RestartReplica = "RestartReplica";
        public const string RestartVM = "RestartVM";

        // Helper Predicates.
        public const string CheckInsideRunInterval = "CheckInsideRunInterval";
        public const string CheckFolderSize = "CheckFolderSize";
        public const string GetRepairHistory = "GetRepairHistory";

        // Resource types.
        public const string ActiveTcpPorts = "ActiveTcpPorts";
        public const string Certificate = "Certificate";
        public const string Cpu = "Cpu";
        public const string CpuPercent = "CpuPercent";
        public const string Disk = "Disk";
        public const string DiskAverageQueueLength = "DiskAverageQueueLength";
        public const string DiskSpaceMB = "DiskSpaceMB";
        public const string DiskSpacePercent = "DiskSpacePercent";
        public const string EphemeralPorts = "EphemeralPorts";
        public const string EndpointUnreachable = "EndpointUnreachable";
        public const string FirewallRules = "FirewallRules";
        public const string Memory = "Memory";
        public const string MemoryMB = "MemoryMB";
        public const string MemoryPercent = "MemoryPercent";
        public const string Network = "Network";
        public const string FileHandles = "FileHandles";
        public const string FileHandlesPercent = "FileHandlesPercent";
    }
}