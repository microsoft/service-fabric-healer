// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Query;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Utilities;

namespace FabricHealer.Repair
{
    public static class UpgradeChecker
    {
        private static readonly Logger Logger = new Logger("UpgradeLogger");

        /// <summary>
        /// Gather list of UD's from all upgrades
        /// </summary>
        /// <param name="fabricClient">FabricClient</param>
        /// <param name="token"></param>
        /// <returns>List of uds</returns>
        public static async Task<IList<int>>
            GetUDsWhereUpgradeInProgressAsync(FabricClient fabricClient, CancellationToken token)
        {
            var domainsWhereUpgradeInProgress = new List<int>();

            domainsWhereUpgradeInProgress.AddRange(
                await GetUdsWhereApplicationUpgradeInProgressAsync(fabricClient, token).ConfigureAwait(true));

            domainsWhereUpgradeInProgress.Add(
                await GetUdsWhereFabricUpgradeInProgressAsync(fabricClient, token).ConfigureAwait(true));

            return domainsWhereUpgradeInProgress;
        }

        /// <summary>
        /// Gets Application Upgrade Domains (integers) for application or applications
        /// currently upgrading (or rolling back).
        /// </summary>
        /// <param name="fabricClient">FabricClient instance</param>
        /// <param name="token">CancellationToken</param>
        /// <param name="appName" type="optional">Application Name (Uri)</param>
        /// <returns>List of integers representing UDs</returns>
        internal static async Task<List<int>>
            GetUdsWhereApplicationUpgradeInProgressAsync(
            FabricClient fabricClient,
            CancellationToken token,
            Uri appName = null)
        {
            try
            {
                ApplicationList appList;
                int currentUpgradeDomainInProgress = -1;
                var upgradeDomainsInProgress = new List<int>();

                if (appName == null)
                {
                    appList = await fabricClient.QueryManager.GetApplicationListAsync().ConfigureAwait(true);
                }
                else
                {
                    appList = await fabricClient.QueryManager.GetApplicationListAsync(
                        appName, 
                        TimeSpan.FromMinutes(1), 
                        token).ConfigureAwait(true);
                }

                foreach (var application in appList)
                {
                    var upgradeProgress =
                        await fabricClient.ApplicationManager.GetApplicationUpgradeProgressAsync(
                            application.ApplicationName, 
                            TimeSpan.FromMinutes(1), 
                            token).ConfigureAwait(true);

                    if (upgradeProgress.UpgradeState.Equals(ApplicationUpgradeState.RollingBackInProgress)
                        || upgradeProgress.UpgradeState.Equals(ApplicationUpgradeState.RollingForwardInProgress)
                        || upgradeProgress.UpgradeState.Equals(ApplicationUpgradeState.RollingForwardPending))
                    {
                        if (int.TryParse(upgradeProgress.CurrentUpgradeDomainProgress.UpgradeDomainName, out currentUpgradeDomainInProgress))
                        {
                            Logger.LogInfo($"Application Upgrade for {application.ApplicationName} is in progress in {currentUpgradeDomainInProgress} upgrade domain.");

                            if (!upgradeDomainsInProgress.Contains(currentUpgradeDomainInProgress))
                            {
                                upgradeDomainsInProgress.Add(currentUpgradeDomainInProgress);
                            }
                        }
                        else
                        {
                            // TryParse fails out value currentUpgradeDomainInProgress will be set to 0, 
                            // 0 is valid UD name, so setting it to -1 to return right value
                            currentUpgradeDomainInProgress = -1;
                        }
                    }
                }
               
                // If no UD's are being upgraded then currentUpgradeDomainInProgress
                // remains -1, otherwise it will be added only once
                if (!upgradeDomainsInProgress.Any())
                {
                    Logger.LogInfo(
                        $"No Application Upgrade is in progress in domain {currentUpgradeDomainInProgress}");

                    upgradeDomainsInProgress.Add(currentUpgradeDomainInProgress);
                }

                return upgradeDomainsInProgress;
            }
            catch (Exception e)
            {
                Logger.LogError(e.ToString());

                return new List<int>{ int.MaxValue };
            }
        }

        /// <summary>
        /// Get the UD where service fabric upgrade is in progress
        /// </summary>
        /// <param name="fabricClient">FabricClient</param>
        /// <param name="token"></param>
        /// <returns>UD in progress</returns>
        public static async Task<int> GetUdsWhereFabricUpgradeInProgressAsync(
            FabricClient fabricClient, 
            CancellationToken token)
        {
            try
            {
                var fabricUpgradeProgress =
                    await fabricClient.ClusterManager.GetFabricUpgradeProgressAsync(
                        FabricHealerManager.ConfigSettings.AsyncTimeout, 
                        token).ConfigureAwait(true);

                int currentUpgradeDomainInProgress = -1;

                if (fabricUpgradeProgress.UpgradeState.Equals(FabricUpgradeState.RollingBackInProgress)
                    || fabricUpgradeProgress.UpgradeState.Equals(FabricUpgradeState.RollingForwardInProgress)
                    || fabricUpgradeProgress.UpgradeState.Equals(FabricUpgradeState.RollingForwardPending))
                {
                    if (int.TryParse(fabricUpgradeProgress.CurrentUpgradeDomainProgress.UpgradeDomainName, out currentUpgradeDomainInProgress))
                    {
                        return currentUpgradeDomainInProgress;
                    }

                    // TryParse fails out value currentUpgradeDomainInProgress will be set to 0, 
                    // 0 is valid UD name, so setting it to -1 to return right value.
                    currentUpgradeDomainInProgress = -1;
                }

                return currentUpgradeDomainInProgress;
            }
            catch (Exception e) when (e is FabricException || e is OperationCanceledException || e is TimeoutException)
            {
                return int.MaxValue;
            }
        }

        /// <summary>
        /// Determines if an Azure tenant update is in progress for cluster VMs.
        /// </summary>
        /// <param name="fabricClient">FabricClient instance</param>
        /// <param name="token">CancellationToken instance</param>
        /// <returns>true if tenant update is in progress, false otherwise</returns>
        public static async Task<bool> IsAzureTenantUpdateInProgress(
            FabricClient fabricClient,
            string nodeType,
            CancellationToken token)
        {
            var repairTasks = await fabricClient.RepairManager.GetRepairTaskListAsync(
                "Azure",
                System.Fabric.Repair.RepairTaskStateFilter.Active | System.Fabric.Repair.RepairTaskStateFilter.Executing,
                $"fabric:/System/InfrastructureService/{nodeType}",
                FabricHealerManager.ConfigSettings.AsyncTimeout,
                token).ConfigureAwait(false);

            bool isAzureTenantRepairInProgress = repairTasks.Count > 0;

            if (isAzureTenantRepairInProgress)
            {
                string message =
                $"Azure Tenant Update in progress. Will not attempt repairs at this time.";

                FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                    LogLevel.Info,
                    $"AzureTenantUpdateInProgress",
                    message,
                    token).GetAwaiter().GetResult();

                return true;
            }

            return false;
        }
    }
}
