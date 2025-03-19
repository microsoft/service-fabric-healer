// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Repair;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Description;
using FabricHealer.Utilities.Telemetry;

namespace FabricHealer.Utilities
{
    public class ConfigSettings
    {
        private readonly StatelessServiceContext context;
        private ConfigurationSettings configSettings;

        public bool EnableAutoMitigation
        {
            get;
            private set;
        }

        public int HealthCheckIntervalInSeconds
        {
            get;
            private set;
        } = 30;

        public bool EnableLogicRuleTracing
        {
            get; private set;
        }

        public bool EnableVerboseLogging
        {
            get;
            private set;
        }

        public bool EnableRollingServiceRestarts
        {
            get;
            private set;
        }

        public bool TelemetryEnabled
        {
            get; set;
        }

        public TelemetryProviderType TelemetryProviderType
        {
            get;
            private set;
        }

        public string AppInsightsConnectionString
        {
            get;
            private set;
        }

        public bool CheckGithubVersion
        {
            get;
            private set;
        }

        // For Azure LogAnalytics Telemetry
        public string LogAnalyticsWorkspaceId
        {
            get;
            private set;
        }

        public string LogAnalyticsSharedKey
        {
            get;
            private set;
        }

        public string LogAnalyticsLogType
        {
            get;
            private set;
        }

        public TimeSpan AsyncTimeout
        {
            get;
            private set;
        } = TimeSpan.FromSeconds(120);

        // For EventSource ETW
        public bool EtwEnabled
        {
            get;
            private set;
        }

        public string LocalLogPathParameter
        {
            get;
            private set;
        }

        public bool OperationalTelemetryEnabled
        {
            get;
            private set;
        }

        // RepairPolicy Enablement\\

        public bool EnableAppRepair
        {
            get;
            set;
        }

        public bool EnableDiskRepair
        {
            get;
            set;
        }

        public bool EnableFabricNodeRepair
        {
            get;
            set;
        }

        public bool EnableReplicaRepair
        {
            get;
            set;
        }

        public bool EnableSystemAppRepair
        {
            get;
            set;
        }

        public bool EnableMachineRepair
        {
            get;
            set;
        }

        public bool EnableCustomRepairPredicateType
        {
            get;
            set;
        }

        public bool EnableCustomServiceInitializers
        {
            get;
            set;
        }

        public ConfigSettings(StatelessServiceContext context)
        {
            this.context = context ?? throw new ArgumentException("ServiceContext can't be null.");
            UpdateConfigSettings();
        }

        internal void UpdateConfigSettings(ConfigurationSettings settings = null)
        {
            configSettings = settings;

            // General
            if (bool.TryParse(GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.EnableAutoMitigation), out bool enableAutoMitigation))
            {
                EnableAutoMitigation = enableAutoMitigation;
            }

            if (int.TryParse(GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.AsyncOperationTimeout), out int timeout))
            {
                AsyncTimeout = TimeSpan.FromSeconds(timeout);
            }

            if (bool.TryParse(GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.EnableCustomServiceInitializers), out bool enableCustomServiceInitializers))
            {
                EnableCustomServiceInitializers = enableCustomServiceInitializers;
            }

            if (bool.TryParse(GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.EnableCustomRepairPredicateType), out bool enableCustomRepairPredicateType))
            {
                EnableCustomRepairPredicateType = enableCustomRepairPredicateType;
            }

            if(bool.TryParse(GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.CheckGithubVersion), out bool checkGithubVersion))
            {
                CheckGithubVersion = checkGithubVersion;
            }

            // Logger
            if (bool.TryParse(GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.EnableVerboseLoggingParameter), out bool enableVerboseLogging))
            {
                EnableVerboseLogging = enableVerboseLogging;
            }

            LocalLogPathParameter = GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.LocalLogPathParameter);

            if (int.TryParse(GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.HealthCheckIntervalInSeconds), out int execFrequency))
            {
                HealthCheckIntervalInSeconds = execFrequency;
            }

            // Rolling service restarts.
            if (bool.TryParse(GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.EnableRollingServiceRestartsParameter), out bool enableRollingRestarts))
            {
                EnableRollingServiceRestarts = enableRollingRestarts;
            }

            // ETW.
            if (bool.TryParse(GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.EnableETW), out bool etwEnabled))
            {
                EtwEnabled = etwEnabled;
            }

            // FabricHealer operational telemetry
            if (bool.TryParse(GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.EnableFabricHealerOperationalTelemetry), out bool fhOpTelemEnabled))
            {
                OperationalTelemetryEnabled = fhOpTelemEnabled;
            }

            // Logic rule predicate tracing.
            if (bool.TryParse(GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.EnableLogicRuleTracing), out bool traceRules))
            {
                EnableLogicRuleTracing = traceRules;
            }

            // Repair Policies
            if (bool.TryParse(GetConfigSettingValue(RepairConstants.AppRepairPolicySectionName, RepairConstants.Enabled), out bool appRepairEnabled))
            {
                EnableAppRepair = appRepairEnabled;
            }

            if (bool.TryParse(GetConfigSettingValue(RepairConstants.DiskRepairPolicySectionName, RepairConstants.Enabled), out bool diskRepairEnabled))
            {
                EnableDiskRepair = diskRepairEnabled;
            }

            if (bool.TryParse(GetConfigSettingValue(RepairConstants.FabricNodeRepairPolicySectionName, RepairConstants.Enabled), out bool nodeRepairEnabled))
            {
                EnableFabricNodeRepair = nodeRepairEnabled;
            }

            if (bool.TryParse(GetConfigSettingValue(RepairConstants.ReplicaRepairPolicySectionName, RepairConstants.Enabled), out bool replicaRepairEnabled))
            {
                EnableReplicaRepair = replicaRepairEnabled;
            }

            if (bool.TryParse(GetConfigSettingValue(RepairConstants.SystemServiceRepairPolicySectionName, RepairConstants.Enabled), out bool systemAppRepairEnabled))
            {
                EnableSystemAppRepair = systemAppRepairEnabled;
            }

            if (bool.TryParse(GetConfigSettingValue(RepairConstants.MachineRepairPolicySectionName, RepairConstants.Enabled), out bool vmRepairEnabled))
            {
                EnableMachineRepair = vmRepairEnabled;
            }

            // Telemetry. Add any new settings above this (note the returns below..).
            if (bool.TryParse(GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.EnableTelemetry), out bool telemEnabled))
            {
                TelemetryEnabled = telemEnabled;

                if (TelemetryEnabled)
                {
                    string telemetryProviderType = GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.TelemetryProviderType);

                    if (string.IsNullOrWhiteSpace(telemetryProviderType))
                    {
                        TelemetryEnabled = false;
                        return;
                    }

                    if (!Enum.TryParse(telemetryProviderType, out TelemetryProviderType telemetryProvider))
                    {
                        TelemetryEnabled = false;
                        return;
                    }

                    TelemetryProviderType = telemetryProvider;

                    if (telemetryProvider == TelemetryProviderType.AzureLogAnalytics)
                    {
                        LogAnalyticsLogType = GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.LogAnalyticsLogTypeParameter);
                        LogAnalyticsSharedKey = GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.LogAnalyticsSharedKeyParameter);
                        LogAnalyticsWorkspaceId = GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.LogAnalyticsWorkspaceIdParameter);

                        if (string.IsNullOrWhiteSpace(LogAnalyticsSharedKey) || string.IsNullOrWhiteSpace(LogAnalyticsSharedKey))
                        {
                            TelemetryEnabled = false;
                            return;
                        }
                    }
                    else
                    {
                        AppInsightsConnectionString = GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.AppInsightsConnectionStringParameter);

                        if (string.IsNullOrWhiteSpace(AppInsightsConnectionString))
                        {
                            TelemetryEnabled = false;
                            return;
                        }
                    }
                }
            }
        }

        private string GetConfigSettingValue(string sectionName, string parameterName)
        {
            try
            {
                var settings = configSettings;

                // This will always be null unless there is a configuration update.
                if (settings == null)
                {
                    settings = context.CodePackageActivationContext?.GetConfigurationPackageObject("Config")?.Settings;

                    if (settings == null)
                    {
                        return null;
                    }
                }

                var section = settings.Sections[sectionName];
                var parameter = section.Parameters[parameterName];

                // reset.
                configSettings = null;

                return parameter.Value;
            }
            catch (Exception e) when (e is KeyNotFoundException or FabricElementNotFoundException)
            {

            }

            return null;
        }
    }
}