﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Microsoft.Win32;
using System;
using System.Fabric;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace FabricHealer.TelemetryLib
{
    /// <summary>
    /// Helper class to facilitate non-PII identification of cluster.
    /// </summary>
    public sealed class ClusterInformation
    {
        private const string FabricRegistryKeyPath = "Software\\Microsoft\\Service Fabric";
        private static string paasClusterId;
        private static string diagnosticsClusterId;
        private static XmlDocument clusterManifestXdoc;
        private static (string ClusterId, string ClusterType, string TenantId) _clusterInfoTuple;
        private static readonly bool isWindows = OperatingSystem.IsWindows();

        public static (string ClusterId, string ClusterType, string TenantId) ClusterInfoTuple
        {
            get
            {
                if (_clusterInfoTuple == (null, null, null))
                {
                    _clusterInfoTuple = TupleGetClusterIdAndTypeAsync(CancellationToken.None).GetAwaiter().GetResult();
                }

                return _clusterInfoTuple;
            }
        }

        /// <summary>
        /// Gets ClusterID, TenantID and ClusterType for current ServiceFabric cluster.
        /// The logic to compute these values closely resembles the logic used in SF runtime's telemetry client.
        /// </summary>
        private static async Task<(string ClusterId, string ClusterType, string TenantId)>
            TupleGetClusterIdAndTypeAsync(CancellationToken token)
        {
            string tenantId = TelemetryConstants.Undefined;
            string clusterId = TelemetryConstants.Undefined;
            string clusterType = TelemetryConstants.Undefined;

            try
            {
                string clusterManifest;
                using (var fc = new FabricClient())
                {
                    clusterManifest = await fc.ClusterManager.GetClusterManifestAsync(
                                                TimeSpan.FromSeconds(TelemetryConstants.AsyncOperationTimeoutSeconds),
                                                token);
                }

                // Get tenantId for PaasV1 clusters or SFRP.
                tenantId = GetTenantId() ?? TelemetryConstants.Undefined;

                if (!string.IsNullOrWhiteSpace(clusterManifest))
                {
                    // Safe XML pattern - *Do not use LoadXml*.
                    clusterManifestXdoc = new XmlDocument { XmlResolver = null };

                    using (var sreader = new StringReader(clusterManifest))
                    {
                        using (var xreader = XmlReader.Create(sreader, new XmlReaderSettings { XmlResolver = null }))
                        {
                            clusterManifestXdoc?.Load(xreader);

                            // Get values from cluster manifest, clusterId if it exists in either Paas or Diagnostics section.
                            GetValuesFromClusterManifest();
                            
                            if (paasClusterId != null)
                            {
                                clusterId = paasClusterId;
                                clusterType = TelemetryConstants.ClusterTypeSfrp;
                            }
                            else if (tenantId != TelemetryConstants.Undefined)
                            {
                                clusterId = tenantId;
                                clusterType = TelemetryConstants.ClusterTypePaasV1;
                            }
                            else if (diagnosticsClusterId != null)
                            {
                                clusterId = diagnosticsClusterId;
                                clusterType = TelemetryConstants.ClusterTypeStandalone;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is FabricException or TimeoutException)
            {

            }

            return (clusterId, clusterType, tenantId);
        }

        /// <summary>
        /// Gets the value of a parameter inside a section from the cluster manifest XmlDocument instance (clusterManifestXdoc).
        /// </summary>
        /// <param name="sectionName"></param>
        /// <param name="parameterName"></param>
        /// <returns></returns>
        private static string GetParamValueFromSection(string sectionName, string parameterName)
        {
            if (clusterManifestXdoc == null)
            {
                return null;
            }

            try
            {
                XmlNode sectionNode = clusterManifestXdoc.DocumentElement?.SelectSingleNode("//*[local-name()='Section' and @Name='" + sectionName + "']");
                XmlNode parameterNode = sectionNode?.SelectSingleNode("//*[local-name()='Parameter' and @Name='" + parameterName + "']");
                XmlAttribute attr = parameterNode?.Attributes?["Value"];

                return attr?.Value;
            }
            catch (System.Xml.XPath.XPathException)
            {
                return null;
            }
        }

        private static string GetClusterIdFromPaasSection()
        {
            return GetParamValueFromSection("Paas", "ClusterId");
        }

        private static string GetClusterIdFromDiagnosticsSection()
        {
            return GetParamValueFromSection("Diagnostics", "ClusterId");
        }

        private static string GetTenantId()
        {
            if (isWindows)
            {
                return GetTenantIdWindows();
            }

            return GetTenantIdLinux();
        }

        private static string GetTenantIdLinux()
        {
            if (isWindows)
            {
                return null;
            }

            // Implementation copied from https://github.com/microsoft/service-fabric/blob/master/src/prod/src/managed/DCA/product/host/TelemetryConsumerLinux.cs
            const string TenantIdFile = "/var/lib/waagent/HostingEnvironmentConfig.xml";

            if (!File.Exists(TenantIdFile))
            {
                return null;
            }

            string tenantId;
            var xmlDoc = new XmlDocument { XmlResolver = null };

            using (var xmlReader = XmlReader.Create(TenantIdFile, new XmlReaderSettings { XmlResolver = null }))
            {
                xmlDoc.Load(xmlReader);
            }

            tenantId = xmlDoc.GetElementsByTagName("Deployment").Item(0).Attributes.GetNamedItem("name").Value;
            return tenantId;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string GetTenantIdWindows()
        {
            if (!isWindows)
            {
                return null;
            }

            const string TenantIdValueName = "WATenantID";

            try
            {

#pragma warning disable CA1416 // Validate platform compatibility
                string tenantIdKeyName = string.Format(CultureInfo.InvariantCulture, "{0}\\{1}", Registry.LocalMachine.Name, FabricRegistryKeyPath);
#pragma warning restore CA1416 // Validate platform compatibility

#pragma warning disable CA1416 // Validate platform compatibility
                return (string)Registry.GetValue(tenantIdKeyName, TenantIdValueName, null);
#pragma warning restore CA1416 // Validate platform compatibility
            }
            catch (Exception e) when (e is ArgumentException or FormatException or IOException or System.Security.SecurityException)
            {
                return null;
            }
        }

        private static void GetValuesFromClusterManifest()
        {
            paasClusterId = GetClusterIdFromPaasSection();
            diagnosticsClusterId = GetClusterIdFromDiagnosticsSection();
        }
    }
}
