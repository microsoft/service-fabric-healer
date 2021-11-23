// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Time;

namespace FabricHealer.Utilities
{
    public sealed class Logger
    {
        private const int Retries = 5;
        private readonly string loggerName;

        // Text file logger.
        private ILogger OLogger
        {
            get; set;
        }

        private string FolderName
        {
            get;
        }

        private string Filename
        {
            get;
        }

        public bool EnableVerboseLogging
        {
            get; set;
        }

        public string LogFolderBasePath
        {
            get; set;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Logger"/> class.
        /// </summary>
        /// <param name="sourceName">Name of observer.</param>
        /// <param name="logFolderBasePath">Base folder path.</param>
        public Logger(string sourceName, string logFolderBasePath = null)
        {
            FolderName = sourceName;
            Filename = sourceName + ".log";
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

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Add current drive letter if not supplied for Windows path target.
                    if (!LogFolderBasePath[..3].Contains(":\\"))
                    {
                        string windrive = Environment.SystemDirectory[..3];
                        logFolderBase = windrive + LogFolderBasePath;
                    }
                }
                else
                {
                    // Remove supplied drive letter if Linux is the runtime target.
                    if (LogFolderBasePath[..3].Contains(":\\"))
                    {
                        logFolderBase = LogFolderBasePath.Remove(0, 3).Replace("\\", "/");
                    }
                }
            }
            else
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    string windrive = Environment.SystemDirectory.Substring(0, 3);
                    logFolderBase = windrive + "fabric_healer_logs";
                }
                else
                {
                    logFolderBase = "/tmp/fabric_healer_logs";
                }
            }

            LogFolderBasePath = logFolderBase;
            string file = Path.Combine(logFolderBase, "fabric_healer.log");

            if (!string.IsNullOrWhiteSpace(FolderName) && !string.IsNullOrWhiteSpace(Filename))
            {
                string folderPath = Path.Combine(logFolderBase, FolderName);
                file = Path.Combine(folderPath, Filename);
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

                var ruleInfo = new LoggingRule(loggerName, NLog.LogLevel.Debug, target);

                LogManager.Configuration.LoggingRules.Add(ruleInfo);
                LogManager.ReconfigExistingLoggers();
            }

            TimeSource.Current = new AccurateUtcTimeSource();
            OLogger = LogManager.GetLogger(loggerName);
        }

        public void LogInfo(string format, params object[] parameters)
        {
            if (!EnableVerboseLogging)
            {
                return;
            }

            OLogger.Info(format, parameters);
        }

        public void LogError(string format, params object[] parameters)
        {
            OLogger.Error(format, parameters);
        }

        public void LogWarning(string format, params object[] parameters)
        {
            OLogger.Warn(format, parameters);
        }

        public static bool TryWriteLogFile(string path, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            for (var i = 0; i < Retries; i++)
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