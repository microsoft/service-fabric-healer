// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics.Tracing;
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

        public string FolderName
        {
            get;
        }

        public string Filename
        {
            get;
        }

        public bool EnableVerboseLogging
        {
            get; set;
        } = false;

        public string LogFolderBasePath
        {
            get; set;
        }

        public string FilePath
        {
            get; set;
        }

        public static EventSource EtwLogger
        {
            get;
        }

        static Logger()
        {
            if (!FabricHealerManager.ConfigSettings.EtwEnabled 
                || string.IsNullOrEmpty(FabricHealerManager.ConfigSettings.EtwProviderName))
            {
                return;
            }

            if (EtwLogger == null)
            {
                EtwLogger = new EventSource(FabricHealerManager.ConfigSettings.EtwProviderName);
            }
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
            this.loggerName = sourceName;

            if (!string.IsNullOrEmpty(logFolderBasePath))
            {
                LogFolderBasePath = logFolderBasePath;
            }

            InitializeLoggers();
        }

        public void InitializeLoggers()
        {
            string logFolderBase;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string windrive = Environment.SystemDirectory.Substring(0, 3);
                logFolderBase = windrive + "fabrichealer_logs";
            }
            else
            {
                logFolderBase = "/tmp/fabrichealer_logs";
            }

            // log directory supplied in config. Set in ObserverManager.
            if (!string.IsNullOrEmpty(this.LogFolderBasePath))
            {
                // Add current drive letter if not supplied for Windows path target.
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (!this.LogFolderBasePath.Substring(0, 3).Contains(":\\"))
                    {
                        string windrive = Environment.SystemDirectory.Substring(0, 3);
                        logFolderBase = windrive + this.LogFolderBasePath;
                    }
                }
                else
                {
                    // Remove supplied drive letter if Linux is the runtime target.
                    if (this.LogFolderBasePath.Substring(0, 3).Contains(":\\"))
                    {
                        this.LogFolderBasePath = this.LogFolderBasePath.Remove(0, 3);
                    }

                    logFolderBase = this.LogFolderBasePath;
                }
            }

            string file = Path.Combine(logFolderBase, "fabrichealer.log");

            if (!string.IsNullOrEmpty(FolderName) && !string.IsNullOrEmpty(Filename))
            {
                string folderPath = Path.Combine(logFolderBase, FolderName);
                file = Path.Combine(folderPath, Filename);
            }

            FilePath = file;

            var targetName = this.loggerName + "LogFile";

            if (LogManager.Configuration == null)
            {
                LogManager.Configuration = new LoggingConfiguration();
            }

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

                LogManager.Configuration.AddTarget(this.loggerName + "LogFile", target);

                var ruleInfo = new LoggingRule(this.loggerName, NLog.LogLevel.Debug, target);

                LogManager.Configuration.LoggingRules.Add(ruleInfo);
                LogManager.ReconfigExistingLoggers();
            }

            TimeSource.Current = new AccurateUtcTimeSource();
            OLogger = LogManager.GetLogger(this.loggerName);
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
            if (string.IsNullOrEmpty(content))
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

        public static void Flush()
        {
            LogManager.Flush();
        }
    }
}