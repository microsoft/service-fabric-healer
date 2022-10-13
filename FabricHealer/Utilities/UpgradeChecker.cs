// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Query;
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
        /// Gets current Application upgrade domains for specified application.
        /// </summary>
        /// <param name="token">CancellationToken</param>
        /// <param name="appName">Application Name (Fabric Uri format)</param>
        /// <returns>UD where application upgrade is currently executing or null if there is no upgrade in progress.</returns>
        internal static async Task<string> GetUDWhereApplicationUpgradeInProgressAsync(Uri appName, CancellationToken token)
        {
            try
            {
                if (appName == null || token.IsCancellationRequested)
                {
                    return null;
                }

                string currentUpgradeDomainInProgress = null;
                ApplicationUpgradeProgress upgradeProgress =
                    await FabricHealerManager.FabricClientSingleton.ApplicationManager.GetApplicationUpgradeProgressAsync(
                            appName,
                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                            token);

                if (upgradeProgress == null)
                {
                    return null;
                }

                if (upgradeProgress.UpgradeState != ApplicationUpgradeState.RollingBackInProgress &&
                    upgradeProgress.UpgradeState != ApplicationUpgradeState.RollingForwardInProgress &&
                    upgradeProgress.UpgradeState != ApplicationUpgradeState.RollingForwardPending)
                {
                    return null;
                }

                currentUpgradeDomainInProgress = upgradeProgress.CurrentUpgradeDomainProgress.UpgradeDomainName;
                return currentUpgradeDomainInProgress;
            }
            catch (Exception e) when (e is ArgumentException || e is FabricException || e is TimeoutException)
            {
                Logger.LogError($"Exception getting UDs for application upgrade in progress for {appName.OriginalString}:{Environment.NewLine}{e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get the UD where service fabric upgrade is in progress
        /// </summary>
        /// <param name="fabricClient">FabricClient</param>
        /// <param name="token"></param>
        /// <returns>UD in progress</returns>
        internal static async Task<string> GetCurrentUDWhereFabricUpgradeInProgressAsync(CancellationToken token)
        {
            try
            {
                FabricUpgradeProgress fabricUpgradeProgress =
                    await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                            () => FabricHealerManager.FabricClientSingleton.ClusterManager.GetFabricUpgradeProgressAsync(
                                    FabricHealerManager.ConfigSettings.AsyncTimeout,
                            token), token);

                if (!fabricUpgradeProgress.UpgradeState.Equals(FabricUpgradeState.RollingBackInProgress) &&
                    !fabricUpgradeProgress.UpgradeState.Equals(FabricUpgradeState.RollingForwardInProgress) &&
                    !fabricUpgradeProgress.UpgradeState.Equals(FabricUpgradeState.RollingForwardPending))
                {
                    return null;
                }

                return fabricUpgradeProgress.CurrentUpgradeDomainProgress.UpgradeDomainName;
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {
                return null;
            }
        }

        /// <summary>
        /// Determines if an Azure tenant update is in progress for cluster VMs.
        /// </summary>
        /// <param name="nodeType">NodeType string</param>
        /// <param name="token">CancellationToken instance</param>
        /// <returns>true if tenant update is in progress, false otherwise</returns>
        internal static async Task<bool> IsAzureUpdateInProgress(string nodeType, string nodeName, CancellationToken token)
        {
            var repairTasks = await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                                        null,
                                        System.Fabric.Repair.RepairTaskStateFilter.Approved |
                                        System.Fabric.Repair.RepairTaskStateFilter.Active |
                                        System.Fabric.Repair.RepairTaskStateFilter.Executing,
                                        $"fabric:/System/InfrastructureService/{nodeType}",
                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                        token);

            bool isAzureTenantRepairInProgress = repairTasks?.Count > 0;

            if (!isAzureTenantRepairInProgress)
            {
                return false;
            }

            if (repairTasks.ToList().Any(
                n => JsonSerializationUtility.TryDeserializeObject(n.ExecutorData, out ISExecutorData data) && data.JobId == nodeName))
            {
                string message = $"Azure Platform or Tenant Update in progress for {nodeType}. Will not attempt repairs at this time.";

                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                        LogLevel.Info,
                        "AzurePlatformOrTenantUpdateInProgress",
                        message,
                        token);

                return true;
            }

            return false;
        }
    }
}
