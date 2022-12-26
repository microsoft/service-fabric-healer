// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Utilities.Telemetry;
using System;
using System.Fabric.Health;

namespace FabricHealer.Utilities
{
    /// <summary>
    /// Reports health data to Service Fabric Health Manager and logs locally (optional).
    /// </summary>
    public class FabricHealthReporter
    {
        private readonly Logger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricHealthReporter"/> class.
        /// </summary>
        /// <param name="fabricClient"></param>
        public FabricHealthReporter(Logger logger)
        {
            _logger = logger;
        }

        public void ReportHealthToServiceFabric(HealthReport healthReport)
        {
            if (healthReport == null)
            {
                return;
            }

            var sendOptions = new HealthReportSendOptions { Immediate = false };

            // Quickly send OK (clears warning/errors states).
            if (healthReport.State == HealthState.Ok)
            {
                sendOptions.Immediate = true;
            }

            var timeToLive = TimeSpan.FromMinutes(5);

            if (healthReport.HealthReportTimeToLive != default)
            {
                timeToLive = healthReport.HealthReportTimeToLive;
            }

            var healthInformation = new HealthInformation(healthReport.SourceId, healthReport.Code ?? healthReport.Property, healthReport.State)
            {
                Description = healthReport.HealthMessage,
                TimeToLive = timeToLive,
                RemoveWhenExpired = true,
            };

            // Local file logging.
            if (healthReport.EmitLogEvent)
            {
                if (healthReport.State == HealthState.Ok)
                {
                    _logger.LogInfo(healthReport.HealthMessage);
                }
                else
                {
                    _logger.LogWarning(healthReport.HealthMessage);
                }
            }

            switch (healthReport.EntityType)
            {
                case EntityType.Application when healthReport.AppName != null:

                    var appHealthReport = new ApplicationHealthReport(healthReport.AppName, healthInformation);
                    FabricHealerManager.FabricClientSingleton.HealthManager.ReportHealth(appHealthReport, sendOptions);
                    break;

                case EntityType.Service when healthReport.ServiceName != null:

                    var serviceHealthReport = new ServiceHealthReport(healthReport.ServiceName, healthInformation);
                    FabricHealerManager.FabricClientSingleton.HealthManager.ReportHealth(serviceHealthReport, sendOptions);
                    break;

                case EntityType.StatefulService when healthReport.PartitionId != Guid.Empty && healthReport.ReplicaOrInstanceId > 0:

                    var statefulServiceHealthReport = new StatefulServiceReplicaHealthReport(healthReport.PartitionId, healthReport.ReplicaOrInstanceId, healthInformation);
                    FabricHealerManager.FabricClientSingleton.HealthManager.ReportHealth(statefulServiceHealthReport, sendOptions);
                    break;

                case EntityType.StatelessService when healthReport.PartitionId != Guid.Empty && healthReport.ReplicaOrInstanceId > 0:

                    var statelessServiceHealthReport = new StatelessServiceInstanceHealthReport(healthReport.PartitionId, healthReport.ReplicaOrInstanceId, healthInformation);
                    FabricHealerManager.FabricClientSingleton.HealthManager.ReportHealth(statelessServiceHealthReport, sendOptions);
                    break;

                case EntityType.Partition when healthReport.PartitionId != Guid.Empty:
                    var partitionHealthReport = new PartitionHealthReport(healthReport.PartitionId, healthInformation);
                    FabricHealerManager.FabricClientSingleton.HealthManager.ReportHealth(partitionHealthReport, sendOptions);
                    break;

                case EntityType.DeployedApplication when healthReport.AppName != null:

                    var deployedApplicationHealthReport = new DeployedApplicationHealthReport(healthReport.AppName, healthReport.NodeName, healthInformation);
                    FabricHealerManager.FabricClientSingleton.HealthManager.ReportHealth(deployedApplicationHealthReport, sendOptions);
                    break;

                case EntityType.Disk:
                case EntityType.Machine:
                case EntityType.Node:

                    var nodeHealthReport = new NodeHealthReport(healthReport.NodeName, healthInformation);
                    FabricHealerManager.FabricClientSingleton.HealthManager.ReportHealth(nodeHealthReport, sendOptions);
                    break;
            }
        }
    }
}
