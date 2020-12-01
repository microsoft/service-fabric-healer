﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Repair;
using System.Collections.Generic;
using System.Linq;

namespace FabricHealer.Utilities
{
    // FabricObserver Error/Warning/Ok Codes.
    public sealed class FabricObserverErrorWarningCodes
    {
        // Ok
        public const string Ok = "FO000";

        // CPU
        public const string AppErrorCpuPercent = "FO001";
        public const string AppWarningCpuPercent = "FO002";
        public const string NodeErrorCpuPercent = "FO003";
        public const string NodeWarningCpuPercent = "FO004";

        // Certificate
        public const string ErrorCertificateExpiration = "FO005";
        public const string WarningCertificateExpiration = "FO006";

        // Disk
        public const string NodeErrorDiskSpacePercent = "FO007";
        public const string NodeErrorDiskSpaceMB = "FO008";
        public const string NodeWarningDiskSpacePercent = "FO009";
        public const string NodeWarningDiskSpaceMB = "FO010";
        public const string NodeErrorDiskAverageQueueLength = "FO011";
        public const string NodeWarningDiskAverageQueueLength = "FO012";

        // Memory
        public const string AppErrorMemoryPercent = "FO013";
        public const string AppWarningMemoryPercent = "FO014";
        public const string AppErrorMemoryMB = "FO015";
        public const string AppWarningMemoryMB = "FO016";
        public const string NodeErrorMemoryPercent = "FO017";
        public const string NodeWarningMemoryPercent = "FO018";
        public const string NodeErrorMemoryMB = "FO019";
        public const string NodeWarningMemoryMB = "FO020";

        // Networking
        public const string AppErrorNetworkEndpointUnreachable = "FO021";
        public const string AppWarningNetworkEndpointUnreachable = "FO022";
        public const string AppErrorTooManyActiveTcpPorts = "FO023";
        public const string AppWarningTooManyActiveTcpPorts = "FO024";
        public const string NodeErrorTooManyActiveTcpPorts = "FO025";
        public const string NodeWarningTooManyActiveTcpPorts = "FO026";
        public const string ErrorTooManyFirewallRules = "FO027";
        public const string WarningTooManyFirewallRules = "FO028";
        public const string AppErrorTooManyActiveEphemeralPorts = "FO029";
        public const string AppWarningTooManyActiveEphemeralPorts = "FO030";
        public const string NodeErrorTooManyActiveEphemeralPorts = "FO031";
        public const string NodeWarningTooManyActiveEphemeralPorts = "FO032";

        public static Dictionary<string, string> AppErrorCodesDictionary { get; } = new Dictionary<string, string>
        {
            { Ok, "Ok" },
            { AppErrorCpuPercent, "AppErrorCpuPercent" },
            { AppWarningCpuPercent, "AppWarningCpuPercent" },
            { AppErrorMemoryPercent, "AppErrorMemoryPercent" },
            { AppWarningMemoryPercent, "AppWarningMemoryPercent" },
            { AppErrorMemoryMB, "AppErrorMemoryMB" },
            { AppWarningMemoryMB, "AppWarningMemoryMB" },
            { AppErrorNetworkEndpointUnreachable, "AppErrorNetworkEndpointUnreachable" },
            { AppWarningNetworkEndpointUnreachable, "AppWarningNetworkEndpointUnreachable" },
            { AppErrorTooManyActiveTcpPorts, "AppErrorTooManyActiveTcpPorts" },
            { AppWarningTooManyActiveTcpPorts, "AppWarningTooManyActiveTcpPorts" },
            { AppErrorTooManyActiveEphemeralPorts, "AppErrorTooManyActiveEphemeralPorts" },
            { AppWarningTooManyActiveEphemeralPorts, "AppWarningTooManyActiveEphemeralPorts" },
        };

        public static Dictionary<string, string> NodeErrorCodesDictionary { get; } = new Dictionary<string, string>
        {
            { Ok, "Ok" },
            { NodeErrorCpuPercent, "NodeErrorCpuPercent" },
            { NodeWarningCpuPercent, "NodeWarningCpuPercent" },
            { ErrorCertificateExpiration, "ErrorCertificateExpiration" },
            { WarningCertificateExpiration, "WarningCertificateExpiration" },
            { NodeErrorDiskSpacePercent, "NodeErrorDiskSpacePercent" },
            { NodeErrorDiskSpaceMB, "NodeErrorDiskSpaceMB" },
            { NodeWarningDiskSpacePercent, "NodeWarningDiskSpacePercent" },
            { NodeWarningDiskSpaceMB, "NodeWarningDiskSpaceMB" },
            { NodeErrorDiskAverageQueueLength, "NodeErrorDiskAverageQueueLength" },
            { NodeWarningDiskAverageQueueLength, "NodeWarningDiskAverageQueueLength" },
            { NodeErrorMemoryPercent, "NodeErrorMemoryPercent" },
            { NodeWarningMemoryPercent, "NodeWarningMemoryPercent" },
            { NodeErrorMemoryMB, "NodeErrorMemoryMB" },
            { NodeWarningMemoryMB, "NodeWarningMemoryMB" },
            { NodeErrorTooManyActiveTcpPorts, "NodeErrorTooManyActiveTcpPorts" },
            { NodeWarningTooManyActiveTcpPorts, "NodeWarningTooManyActiveTcpPorts" },
            { ErrorTooManyFirewallRules, "NodeErrorTooManyFirewallRules" },
            { WarningTooManyFirewallRules, "NodeWarningTooManyFirewallRules" },
            { NodeErrorTooManyActiveEphemeralPorts, "NodeErrorTooManyActiveEphemeralPorts" },
            { NodeWarningTooManyActiveEphemeralPorts, "NodeWarningTooManyActiveEphemeralPorts" },
        };

        public static string GetErrorWarningNameFromCode(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            if (AppErrorCodesDictionary.Any(k => k.Key == id))
            {
                return AppErrorCodesDictionary.First(k => k.Key == id).Value;
            }

            if (NodeErrorCodesDictionary.Any(k => k.Key == id))
            {
                return NodeErrorCodesDictionary.First(k => k.Key == id).Value;
            }

            return null;
        }

        public static string GetMetricNameFromCode(string code)
        {
            if (GetIsResourceType(code, RepairConstants.ActiveTcpPorts))
            {
                return RepairConstants.ActiveTcpPorts;
            }

            if (GetIsResourceType(code, RepairConstants.Certificate))
            {
                return RepairConstants.Certificate;
            }

            if (GetIsResourceType(code, RepairConstants.Cpu))
            {
                return RepairConstants.CpuPercent;
            }

            if (GetIsResourceType(code, RepairConstants.DiskAverageQueueLength))
            {
                return RepairConstants.DiskAverageQueueLength;
            }

            if (GetIsResourceType(code, RepairConstants.DiskSpaceMB))
            {
                return RepairConstants.DiskSpaceMB;
            }

            if (GetIsResourceType(code, RepairConstants.DiskSpacePercent))
            {
                return RepairConstants.DiskSpacePercent;
            }

            if (GetIsResourceType(code, RepairConstants.EndpointUnreachable))
            {
                return RepairConstants.EndpointUnreachable;
            }

            if (GetIsResourceType(code, RepairConstants.EphemeralPorts))
            {
                return RepairConstants.EphemeralPorts;
            }

            if (GetIsResourceType(code, RepairConstants.FirewallRules))
            {
                return RepairConstants.FirewallRules;
            }

            if (GetIsResourceType(code, RepairConstants.MemoryMB))
            {
                return RepairConstants.MemoryMB;
            }

            return GetIsResourceType(code, RepairConstants.MemoryPercent) ? RepairConstants.MemoryPercent : null;
        }

        private static bool GetIsResourceType(string id, string resourceType)
        {
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }

            if (AppErrorCodesDictionary.Any(k => k.Key == id && k.Value.Contains(resourceType)))
            {
                return true;
            }

            if (NodeErrorCodesDictionary.Any(k => k.Key == id && k.Value.Contains(resourceType)))
            {
                return true;
            }

            return false;
        }
    }
}
