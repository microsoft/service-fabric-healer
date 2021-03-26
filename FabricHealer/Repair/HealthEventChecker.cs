// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric.Health;
using FabricHealer.Utilities;

namespace FabricHealer.Repair
{
    public static class HealthEventChecker
    {
        public static bool IsHealthPropertyInError(HealthEvent healthEvent,  HealthReportKind kind, bool treatWarningAsError = false)
        {
            if (healthEvent?.HealthInformation.HealthState != HealthState.Error && !treatWarningAsError)
            {
                return false;
            }

            switch (kind)
            {
                // App, Service, Replica/Instance, CodePackage, etc (as in service process/instance scope)
                case HealthReportKind.Application:
                case HealthReportKind.Service:
                case HealthReportKind.DeployedApplication:
                case HealthReportKind.StatefulServiceReplica:
                case HealthReportKind.StatelessServiceInstance:
                case HealthReportKind.DeployedServicePackage:
                    return healthEvent != null && FOErrorWarningCodes.AppErrorCodesDictionary.ContainsKey(healthEvent.HealthInformation.SourceId);
                
                    // Node level (as in VM scope)
                case HealthReportKind.Node:
                    return healthEvent != null && FOErrorWarningCodes.NodeErrorCodesDictionary.ContainsKey(healthEvent.HealthInformation.SourceId);
                
                case HealthReportKind.Invalid:
                    break;
                
                case HealthReportKind.Partition:
                    break;
                
                case HealthReportKind.Cluster:
                    break;
                
                default:
                    return false;
            }

            return false;
        }

        public static bool HasHealthPropertyExpired(HealthEvent healthEvent)
        {
            if (healthEvent == null)
            {
                throw new ArgumentException("HealthEvent can't be null.");
            }

            return healthEvent.IsExpired;
        }
    }
}
