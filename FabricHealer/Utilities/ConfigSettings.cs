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

        public int ExecutionLoopSleepSeconds
        {
            get;
            private set;
        } = 30;

        public bool EnableVerboseLogging
        {
            get;
            private set;
        }

        public bool TelemetryEnabled
        {
            get;
            private set;
        }

        public TelemetryProviderType TelemetryProvider
        {
            get;
            private set;
        }

        // For Azure ApplicationInsights Telemetry
        public string AppInsightsInstrumentationKey
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

        // For EventSource Telemetry
        public bool EtwEnabled
        {
            get;
            private set;
        }

        // For EventSource Telemetry
        public string EtwProviderName
        {
            get;
            private set;
        }

        public string LocalLogPathParameter
        {
            get;
            private set;
        }

        // RepairPolicy Enablement
        public bool EnableAppRepair
        {
            get;
            private set;
        }

        public bool EnableDiskRepair
        {
            get;
            private set;
        }

        public bool EnableNodeRepair
        {
            get;
            private set;
        }

        public bool EnableReplicaRepair
        {
            get;
            private set;
        }

        public bool EnableSystemAppRepair
        {
            get;
            private set;
        }

        public bool EnableVmRepair
        {
            get;
            private set;
        }

        public bool OperationalTelemetryEnabled 
        { 
            get; private set; 
        }

        public ConfigSettings(StatelessServiceContext context)
        {
            this.context = context ?? throw new ArgumentException("Context can't be null.");
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

            // Logger
            if (bool.TryParse(GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.EnableVerboseLoggingParameter), out bool enableVerboseLogging))
            {
                EnableVerboseLogging = enableVerboseLogging;
            }

            LocalLogPathParameter = GetConfigSettingValue( RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.LocalLogPathParameter);

            if (int.TryParse( GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.HealthCheckLoopSleepTimeSeconds), out int execFrequency))
            {
                ExecutionLoopSleepSeconds = execFrequency;
            }

            // Telemetry.
            if (bool.TryParse(GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.AppInsightsTelemetryEnabled), out bool telemEnabled))
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

                    if (Enum.TryParse(telemetryProviderType, out TelemetryProviderType telemetryProvider))
                    {
                        TelemetryProvider = telemetryProvider;

                        if (telemetryProvider == TelemetryProviderType.AzureLogAnalytics)
                        {
                            LogAnalyticsLogType = GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.LogAnalyticsLogTypeParameter);
                            LogAnalyticsSharedKey = GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.LogAnalyticsSharedKeyParameter);
                            LogAnalyticsWorkspaceId = GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.LogAnalyticsWorkspaceIdParameter);
                        }
                        else
                        {
                            AppInsightsInstrumentationKey = GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.AppInsightsInstrumentationKeyParameter);
                        }
                    }
                }
            }

            // FabricHealer ETW telemetry.
            if (bool.TryParse(GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.EnableEventSourceProvider), out bool etwEnabled))
            {
                EtwEnabled = etwEnabled;
                EtwProviderName = GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.EventSourceProviderName);
            }

            // FabricHealer operational telemetry
            if (bool.TryParse(GetConfigSettingValue(RepairConstants.RepairManagerConfigurationSectionName, RepairConstants.EnableFabricHealerOperationalTelemetry), out bool fhOpTelemEnabled))
            {
                OperationalTelemetryEnabled = fhOpTelemEnabled;
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
                EnableNodeRepair = nodeRepairEnabled;
            }

            if (bool.TryParse(GetConfigSettingValue(RepairConstants.ReplicaRepairPolicySectionName, RepairConstants.Enabled), out bool replicaRepairEnabled))
            {
                EnableReplicaRepair = replicaRepairEnabled;
            }

            if (bool.TryParse(GetConfigSettingValue(RepairConstants.SystemAppRepairPolicySectionName, RepairConstants.Enabled), out bool systemAppRepairEnabled))
            {
                EnableSystemAppRepair = systemAppRepairEnabled;
            }

            if (bool.TryParse(GetConfigSettingValue(RepairConstants.VmRepairPolicySectionName, RepairConstants.Enabled), out bool vmRepairEnabled))
            {
                EnableVmRepair = vmRepairEnabled;
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
            catch (Exception e) when (e is KeyNotFoundException || e is FabricElementNotFoundException)
            {

            }

            return null;
        }
    }
}