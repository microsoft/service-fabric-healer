// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Repair;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Utilities;

namespace FabricHealer.Repair
{
    public static class UpgradeChecker
    {
        private static readonly Logger Logger = new("UpgradeLogger");

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

                if (upgradeProgress.UpgradeState is not ApplicationUpgradeState.RollingBackInProgress and
                    not ApplicationUpgradeState.RollingForwardInProgress and
                    not ApplicationUpgradeState.RollingForwardPending)
                {
                    return null;
                }

                currentUpgradeDomainInProgress = upgradeProgress.CurrentUpgradeDomainProgress.UpgradeDomainName;
                return currentUpgradeDomainInProgress;
            }
            catch (Exception e) when (e is ArgumentException or FabricException or TimeoutException)
            {
                Logger.LogWarning($"Handled Exception getting UDs for application upgrade in progress for " +
                                  $"{appName.OriginalString}:{Environment.NewLine}{e.Message}");
                return null;
            }
            catch (Exception e) // This call should not crash FO. Log the error, fix the bug, if any.
            {
                Logger.LogError($"Unhandled Exception getting UDs for application upgrade in progress for " +
                $"{appName.OriginalString}:{e.Message}");

                if (e is OutOfMemoryException)
                {
                    // Terminate now.
                    Environment.FailFast($"FH hit OOM:{Environment.NewLine}{Environment.StackTrace}");
                }

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
            catch (Exception e) when (e is FabricException or TimeoutException)
            {
                Logger.LogWarning($"Handled Exception in GetCurrentUDWhereFabricUpgradeInProgressAsync: {e.Message}");
                return null;
            }
            catch (Exception e) // This call should not crash FO. Log the error, fix the bug, if any.
            {
                Logger.LogError($"Unhandled Exception in GetCurrentUDWhereFabricUpgradeInProgressAsync: {e.Message}");
                
                if (e is OutOfMemoryException)
                {
                    // Terminate now.
                    Environment.FailFast($"FH hit OOM:{Environment.NewLine}{Environment.StackTrace}");
                }

                return null;
            }
        }

        /// <summary>
        /// Determines if an Azure tenant/platform update is in progress in the cluster.
        /// </summary>
        /// <param name="token">CancellationToken instance</param>
        /// <returns>true if tenant update is in progress, false otherwise</returns>
        internal static async Task<bool> IsAzureJobInProgressAsync(string nodeName, CancellationToken token)
        {
            try
            {
                var repairTasks = await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                                            RepairConstants.AzureTaskIdPrefix,
                                            RepairTaskStateFilter.Active,
                                            null,
                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                            token);

                bool isAzureTenantRepairInProgress = repairTasks?.Count > 0;

                if (!isAzureTenantRepairInProgress)
                {
                    return false;
                }

                foreach (var repair in repairTasks)
                {
                    // Job state is at least Approved.
                    if (repair.Impact is NodeRepairImpactDescription impact)
                    {
                        if (!impact.ImpactedNodes.Any(n => n.NodeName == nodeName))
                        {
                            continue;
                        }

                        string message = $"Azure Platform or Tenant Update in progress for {nodeName}. Will not attempt repairs at this time.";

                        await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                $"AzurePlatformOrTenantUpdateInProgress_{nodeName}",
                                message,
                                token);

                        return true;
                    }
                    // Job state is Created/Claimed if we get here (there is no Impact established yet).
                    else if (repair.Target is NodeRepairTargetDescription target)
                    {
                        if (!target.Nodes.Any(n => n == nodeName))
                        {
                            continue;
                        }

                        string message = $"Azure Platform or Tenant Update in progress for {nodeName}. Will not attempt repairs at this time.";

                        await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                LogLevel.Info,
                                $"AzurePlatformOrTenantUpdateInProgress_{nodeName}",
                                message,
                                token);

                        return true;
                    }
                }
            }
            catch (Exception e) when (e is FabricException or TimeoutException)
            {
                Logger.LogWarning($"Handled Exception in IsAzureJobInProgressAsync: {e.Message}");
                return false;
            }
            catch (Exception e) // This call should not crash FO. Log the error, fix the bug, if any.
            {
                Logger.LogError($"Unhandled Exception in IsAzureJobInProgressAsync: {e.Message}");

                if (e is OutOfMemoryException)
                {
                    // Terminate now.
                    Environment.FailFast($"FH hit OOM:{Environment.NewLine}{Environment.StackTrace}");
                }

                return false;
            }

            return false;
        }
    }
}
