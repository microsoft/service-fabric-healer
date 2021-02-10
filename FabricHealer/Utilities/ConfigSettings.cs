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
            get; set;
        }

        public int ExecutionLoopSleepSeconds
        {
            get; set;
        } = 30;

        public bool EnableVerboseLocalLogging
        {
            get; set;
        }

        public bool TelemetryEnabled
        {
            get; set;
        }

        public TelemetryProviderType TelemetryProvider
        {
            get; set;
        }

        // For Azure ApplicationInsights Telemetry
        public string AppInsightsInstrumentationKey
        {
            get; set;
        }

        // For Azure LogAnalytics Telemetry
        public string LogAnalyticsWorkspaceId
        {
            get; set;
        }

        public string LogAnalyticsSharedKey
        {
            get; set;
        }

        public string LogAnalyticsLogType
        {
            get; set;
        }

        public TimeSpan AsyncTimeout
        {
            get; set;
        } = TimeSpan.FromSeconds(120);

        // For EventSource Telemetry
        public bool EtwEnabled
        {
            get; set;
        }

        // For EventSource Telemetry
        public string EtwProviderName
        {
            get; set;
        }

        public string LocalLogPathParameter
        {
            get; set;
        }

        // RepairPolicy Enablement
        public bool EnableAppRepair
        {
            get; set;
        }

        public bool EnableNodeRepair
        {
            get; set;
        }

        public bool EnableReplicaRepair
        {
            get; set;
        }

        public bool EnableSystemAppRepair
        {
            get; set;
        }

        public bool EnableVmRepair
        {
            get; set;
        }

        public ConfigSettings(StatelessServiceContext context)
        {
            this.context = context ?? throw new ArgumentException("Context can't be null.");

            UpdateConfigSettings();
        }

        internal void UpdateConfigSettings(ConfigurationSettings settings = null)
        {
            this.configSettings = settings;

            // General
            if (bool.TryParse(
                GetConfigSettingValue(
                    RepairConstants.RepairManagerConfigurationSectionName,
                    RepairConstants.EnableAutoMitigation),
                out bool enableAutoMitigation))
            {
                EnableAutoMitigation = enableAutoMitigation;
            }
            
            if (int.TryParse(GetConfigSettingValue(
                RepairConstants.RepairManagerConfigurationSectionName,
                RepairConstants.AsyncOperationTimeout),
                out int timeout))
            {
                AsyncTimeout = TimeSpan.FromSeconds(timeout);
            }

            // Logger
            if (bool.TryParse(
                GetConfigSettingValue(
                RepairConstants.RepairManagerConfigurationSectionName,
                RepairConstants.EnableVerboseLoggingParameter),
                out bool enableVerboseLogging))
            {
                EnableVerboseLocalLogging = enableVerboseLogging;
            }

            LocalLogPathParameter = GetConfigSettingValue(
                RepairConstants.RepairManagerConfigurationSectionName,
                RepairConstants.LocalLogPathParameter);

            if (int.TryParse(
                GetConfigSettingValue(
                    RepairConstants.RepairManagerConfigurationSectionName,
                    RepairConstants.HealthCheckLoopSleepTimeSeconds),
                out int execFrequency))
            {
                ExecutionLoopSleepSeconds = execFrequency;
            }

            // (Assuming Diagnostics/Analytics cloud service implemented) Telemetry.
            if (bool.TryParse(GetConfigSettingValue(
                RepairConstants.RepairManagerConfigurationSectionName,
                RepairConstants.AppInsightsTelemetryEnabled), 
                out bool telemEnabled))
            {
                TelemetryEnabled = telemEnabled;

                if (TelemetryEnabled)
                {
                    string telemetryProviderType = GetConfigSettingValue(
                        RepairConstants.RepairManagerConfigurationSectionName,
                        RepairConstants.TelemetryProviderType);
                    
                    if (string.IsNullOrEmpty(telemetryProviderType))
                    {
                        TelemetryEnabled = false;
                        
                        return;
                    }

                    if (Enum.TryParse(telemetryProviderType, out TelemetryProviderType telemetryProvider))
                    {
                        TelemetryProvider = telemetryProvider;

                        if (telemetryProvider == TelemetryProviderType.AzureLogAnalytics)
                        {
                            LogAnalyticsLogType = GetConfigSettingValue(
                                RepairConstants.RepairManagerConfigurationSectionName,
                                RepairConstants.LogAnalyticsLogTypeParameter);

                            LogAnalyticsSharedKey = GetConfigSettingValue(
                                RepairConstants.RepairManagerConfigurationSectionName,
                                RepairConstants.LogAnalyticsSharedKeyParameter);

                            LogAnalyticsWorkspaceId = GetConfigSettingValue(
                                RepairConstants.RepairManagerConfigurationSectionName,
                                RepairConstants.LogAnalyticsWorkspaceIdParameter);
                        }
                        else
                        {
                            AppInsightsInstrumentationKey = GetConfigSettingValue(
                                RepairConstants.RepairManagerConfigurationSectionName,
                                RepairConstants.AppInsightsInstrumentationKeyParameter);
                        }
                    }
                }
            }

            // FabricHealer ETW telemetry.
            if (bool.TryParse(GetConfigSettingValue(
                RepairConstants.RepairManagerConfigurationSectionName,
                RepairConstants.EnableEventSourceProvider),
                out bool etwEnabled))
            {
                EtwEnabled = etwEnabled;
                EtwProviderName = GetConfigSettingValue(
                    RepairConstants.RepairManagerConfigurationSectionName,
                    RepairConstants.EventSourceProviderName);
            }

            // Repair Policies
            if (bool.TryParse(GetConfigSettingValue(
                RepairConstants.AppRepairPolicySectionName,
                RepairConstants.Enabled),
                out bool appRepairEnabled))
            {
                this.EnableAppRepair = appRepairEnabled;
            }

            if (bool.TryParse(GetConfigSettingValue(
                RepairConstants.FabricNodeRepairPolicySectionName,
                RepairConstants.Enabled),
                out bool nodeRepairEnabled))
            {
                this.EnableNodeRepair = nodeRepairEnabled;
            }

            if (bool.TryParse(GetConfigSettingValue(
                RepairConstants.ReplicaRepairPolicySectionName,
                RepairConstants.Enabled),
                out bool replicaRepairEnabled))
            {
                this.EnableReplicaRepair = replicaRepairEnabled;
            }

            if (bool.TryParse(GetConfigSettingValue(
                RepairConstants.SystemAppRepairPolicySectionName,
                RepairConstants.Enabled),
                out bool systemAppRepairEnabled))
            {
                this.EnableSystemAppRepair = systemAppRepairEnabled;
            }

            if (bool.TryParse(GetConfigSettingValue(
                RepairConstants.VmRepairPolicySectionName,
                RepairConstants.Enabled),
                out bool vmRepairEnabled))
            {
                this.EnableVmRepair = vmRepairEnabled;
            }
        }

        private string GetConfigSettingValue(string sectionName, string parameterName)
        {
            try
            {
                var settings = this.configSettings;

                // This will always be null unless there is a configuration update.
                if (settings == null)
                {
                    settings = this.context.CodePackageActivationContext?.GetConfigurationPackageObject("Config")?.Settings;
                    
                    if (settings == null)
                    {
                        return null;
                    }
                }

                var section = settings.Sections[sectionName];
                var parameter = section.Parameters[parameterName];

                // reset.
                this.configSettings = null;

                return parameter.Value;  
            }
            catch (KeyNotFoundException)
            {

            }
            catch (FabricElementNotFoundException)
            {
    
            }

            return null;
        }
    }
}