﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Utilities;

namespace FabricHealer.Repair
{
    public static class UpgradeChecker
    {
        private static readonly Logger Logger = new Logger("UpgradeLogger");

        /// <summary>
        /// Gets Application Upgrade Domains (integers) for application or applications
        /// currently upgrading (or rolling back).
        /// </summary>
        /// <param name="fabricClient">FabricClient instance</param>
        /// <param name="token">CancellationToken</param>
        /// <param name="appName" type="optional">Application Name (Uri)</param>
        /// <returns>List of integers representing UDs</returns>
        internal static async Task<List<int>> GetUdsWhereApplicationUpgradeInProgressAsync(FabricClient fabricClient, Uri appName, CancellationToken token)
        {
            try
            {
                if (appName == null)
                {
                    throw new ArgumentException("appName must be supplied.");
                }

                int currentUpgradeDomainInProgress = -1;
                var upgradeDomainsInProgress = new List<int>();


                var appList = await fabricClient.QueryManager.GetApplicationListAsync(appName, FabricHealerManager.ConfigSettings.AsyncTimeout, token).ConfigureAwait(false);

                foreach (var application in appList)
                {
                    var upgradeProgress =
                        await fabricClient.ApplicationManager.GetApplicationUpgradeProgressAsync(
                                                                application.ApplicationName, 
                                                                TimeSpan.FromMinutes(1), 
                                                                token).ConfigureAwait(false);

                    if (!upgradeProgress.UpgradeState.Equals(ApplicationUpgradeState.RollingBackInProgress) &&
                        !upgradeProgress.UpgradeState.Equals(ApplicationUpgradeState.RollingForwardInProgress) &&
                        !upgradeProgress.UpgradeState.Equals(ApplicationUpgradeState.RollingForwardPending))
                    {
                        continue;
                    }

                    if (int.TryParse(upgradeProgress.CurrentUpgradeDomainProgress.UpgradeDomainName, out currentUpgradeDomainInProgress))
                    {
                        if (!upgradeDomainsInProgress.Contains(currentUpgradeDomainInProgress))
                        {
                            upgradeDomainsInProgress.Add(currentUpgradeDomainInProgress);
                        }
                    }
                    else
                    {
                        currentUpgradeDomainInProgress = -1;
                    }
                }
               
                // If no UD's are being upgraded then currentUpgradeDomainInProgress
                // remains -1, otherwise it will be added only once.
                if (!upgradeDomainsInProgress.Any())
                {
                    upgradeDomainsInProgress.Add(currentUpgradeDomainInProgress);
                }

                return upgradeDomainsInProgress;
            }
            catch (Exception e) when (e is ArgumentException || e is FabricException || e is TimeoutException)
            {
                Logger.LogError($"Exception getting UDs for application upgrades in progress:{Environment.NewLine}{e}");

                return new List<int>{ -1 };
            }
        }

        /// <summary>
        /// Get the UD where service fabric upgrade is in progress
        /// </summary>
        /// <param name="fabricClient">FabricClient</param>
        /// <param name="token"></param>
        /// <returns>UD in progress</returns>
        public static async Task<int> GetUdsWhereFabricUpgradeInProgressAsync(FabricClient fabricClient, CancellationToken token)
        {
            try
            {
                FabricUpgradeProgress fabricUpgradeProgress =
                    await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                    () => fabricClient.ClusterManager.GetFabricUpgradeProgressAsync(
                                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                    token), token).ConfigureAwait(false);

                if (!fabricUpgradeProgress.UpgradeState.Equals(FabricUpgradeState.RollingBackInProgress) &&
                    !fabricUpgradeProgress.UpgradeState.Equals(FabricUpgradeState.RollingForwardInProgress) &&
                    !fabricUpgradeProgress.UpgradeState.Equals(FabricUpgradeState.RollingForwardPending))
                {
                    return -1;
                }

                return int.TryParse(fabricUpgradeProgress.CurrentUpgradeDomainProgress.UpgradeDomainName, out int currentUpgradeDomainInProgress) ? currentUpgradeDomainInProgress : -1;
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {
                return -1;
            }
        }

        /// <summary>
        /// Determines if an Azure tenant update is in progress for cluster VMs.
        /// </summary>
        /// <param name="fabricClient">FabricClient instance</param>
        /// <param name="nodeType">NodeType string</param>
        /// <param name="token">CancellationToken instance</param>
        /// <returns>true if tenant update is in progress, false otherwise</returns>
        public static async Task<bool> IsAzureTenantUpdateInProgress(FabricClient fabricClient, string nodeType, CancellationToken token)
        {
            var repairTasks = await fabricClient.RepairManager.GetRepairTaskListAsync(
                                                                "Azure",
                                                                System.Fabric.Repair.RepairTaskStateFilter.Active | System.Fabric.Repair.RepairTaskStateFilter.Executing,
                                                                $"fabric:/System/InfrastructureService/{nodeType}",
                                                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                token).ConfigureAwait(false);

            bool isAzureTenantRepairInProgress = repairTasks.Count > 0;

            if (!isAzureTenantRepairInProgress)
            {
                return false;
            }

            string message = "Azure Tenant Update in progress. Will not attempt repairs at this time.";

            await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                            LogLevel.Info,
                                                            "AzureTenantUpdateInProgress",
                                                            message,
                                                            token).ConfigureAwait(false);
            return true;
        }
    }
}
