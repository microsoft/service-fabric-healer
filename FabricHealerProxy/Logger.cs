// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.IO;
using System.Threading;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Time;

namespace FabricHealer
{
    internal sealed class Logger
    {
        private const int MaxRetries = 5;

        // Text file logger.
        private ILogger logger;
        private readonly string folderName;
        private readonly string filename;
        private readonly string loggerName;

        internal bool EnableVerboseLogging
        {
            get; set;
        }

        internal string LogFolderBasePath
        {
            get; set;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Logger"/> class.
        /// </summary>
        /// <param name="sourceName">Name of observer.</param>
        /// <param name="logFolderBasePath">Base folder path.</param>
        internal Logger(string sourceName, string logFolderBasePath = null)
        {
            folderName = sourceName;
            filename = sourceName + ".log";
            loggerName = sourceName;

            if (!string.IsNullOrWhiteSpace(logFolderBasePath))
            {
                LogFolderBasePath = logFolderBasePath;
            }

            InitializeLoggers();
        }

        private void InitializeLoggers()
        {
            // default log directory.
            string logFolderBase;

            // Log directory supplied in Settings.xml.
            if (!string.IsNullOrEmpty(LogFolderBasePath))
            {
                logFolderBase = LogFolderBasePath;

                if (OperatingSystem.IsWindows())
                {
                    // Add current drive letter if not supplied for Windows path target.
                    if (!LogFolderBasePath.Substring(0, 3).Contains(":\\"))
                    {
                        string windrive = Environment.SystemDirectory.Substring(0, 3);
                        logFolderBase = windrive + LogFolderBasePath;
                    }
                }
                else
                {
                    // Remove supplied drive letter if Linux is the runtime target.
                    if (LogFolderBasePath.Substring(0, 3).Contains(":\\"))
                    {
                        logFolderBase = LogFolderBasePath.Remove(0, 3).Replace("\\", "/");
                    }
                }
            }
            else
            {
                if (OperatingSystem.IsWindows())
                {
                    string windrive = Environment.SystemDirectory.Substring(0, 3);
                    logFolderBase = windrive + "fabric_healer_proxy_logs";
                }
                else
                {
                    logFolderBase = "/tmp/fabric_healer_proxy_logs";
                }
            }

            LogFolderBasePath = logFolderBase;
            string file = Path.Combine(logFolderBase, "fabric_healer_proxy.log");

            if (!string.IsNullOrWhiteSpace(folderName) && !string.IsNullOrWhiteSpace(filename))
            {
                string folderPath = Path.Combine(logFolderBase, folderName);
                file = Path.Combine(folderPath, filename);
            }

            var targetName = loggerName + "LogFile";

            LogManager.Configuration ??= new LoggingConfiguration();

            if ((FileTarget)LogManager.Configuration?.FindTargetByName(targetName) == null)
            {
                var target = new FileTarget
                {
                    Name = targetName,
                    FileName = file,
                    Layout = "${longdate}--${uppercase:${level}}--${message}",
                    OpenFileCacheTimeout = 5,
                    ArchiveNumbering = ArchiveNumberingMode.DateAndSequence,
                    ArchiveEvery = FileArchivePeriod.Day,
                    AutoFlush = true,
                };

                LogManager.Configuration.AddTarget(loggerName + "LogFile", target);

                var ruleInfo = new LoggingRule(loggerName, LogLevel.Debug, target);

                LogManager.Configuration.LoggingRules.Add(ruleInfo);
                LogManager.ReconfigExistingLoggers();
            }

            TimeSource.Current = new AccurateUtcTimeSource();
            logger = LogManager.GetLogger(loggerName);
        }

        internal void LogInfo(string format, params object[] parameters)
        {
            if (!EnableVerboseLogging)
            {
                return;
            }

            logger.Info(format, parameters);
        }

        internal void LogError(string format, params object[] parameters)
        {
            logger.Error(format, parameters);
        }

        internal void LogWarning(string format, params object[] parameters)
        {
            logger.Warn(format, parameters);
        }

        internal static bool TryWriteLogFile(string path, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            for (var i = 0; i < MaxRetries; i++)
            {
                try
                {
                    string directory = Path.GetDirectoryName(path);
                    
                    if (directory != null && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.WriteAllText(path, content);
                    
                    return true;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                }
           
                Thread.Sleep(1000);
            }

            return false;
        }
    }
}