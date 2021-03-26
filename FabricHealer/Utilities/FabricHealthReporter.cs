// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Health;

namespace FabricHealer.Utilities
{
    /// <summary>
    /// Reports health data to Service Fabric Health Manager and logs locally (optional).
    /// </summary>
    public class FabricHealthReporter
    {
        private readonly FabricClient fabricClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricHealthReporter"/> class.
        /// </summary>
        /// <param name="fabricClient"></param>
        public FabricHealthReporter(FabricClient fabricClient)
        {
            this.fabricClient = fabricClient ?? throw new ArgumentException("FabricClient can't be null");
            this.fabricClient.Settings.HealthReportSendInterval = TimeSpan.FromSeconds(1);
            this.fabricClient.Settings.HealthReportRetrySendInterval = TimeSpan.FromSeconds(3);
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

            var healthInformation = new HealthInformation(
                                            healthReport.Source,
                                            healthReport.Code ?? healthReport.Property,
                                            healthReport.State)
            {
                Description = healthReport.HealthMessage,
                TimeToLive = timeToLive,
                RemoveWhenExpired = true,
            };

            switch (healthReport.ReportType)
            {
                case HealthReportType.Application when healthReport.AppName != null:
                
                    var appHealthReport = new ApplicationHealthReport(healthReport.AppName, healthInformation);
                    fabricClient.HealthManager.ReportHealth(appHealthReport, sendOptions);
                    break;
                
                case HealthReportType.Node when healthReport.NodeName != null:
                
                    var nodeHealthReport = new NodeHealthReport(healthReport.NodeName, healthInformation);
                    fabricClient.HealthManager.ReportHealth(nodeHealthReport, sendOptions);
                    break;
                
                case HealthReportType.Service when healthReport.ServiceName != null:
                
                    var serviceHealthReport = new ServiceHealthReport(healthReport.ServiceName, healthInformation);
                    fabricClient.HealthManager.ReportHealth(serviceHealthReport, sendOptions);
                    break;
                
                default:
                    break;
            }
        }
    }

    public enum HealthReportType
    {
        Application,
        Node,
        Service
    }
}
