// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics.Tracing;
using System.Fabric.Health;
using System.IO;
using System.Threading;
using FabricHealer.Utilities.Telemetry;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Time;

namespace FabricHealer.Utilities
{
    public sealed class Logger
    {
        private const int Retries = 5;
        private const int MaxArchiveFileLifetimeDays = 7;
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

                if (OperatingSystem.IsWindows())
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
                if (OperatingSystem.IsWindows())
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
                    //ConcurrentWrites = true,
                    EnableFileDelete = true,
                    FileName = file,
                    Layout = "${longdate}--${uppercase:${level}}--${message}",
                    OpenFileCacheTimeout = 5,
                    ArchiveEvery = FileArchivePeriod.Day,
                    ArchiveNumbering = ArchiveNumberingMode.DateAndSequence,
                    MaxArchiveDays = MaxArchiveFileLifetimeDays,
                    AutoFlush = true
                };

                LogManager.Configuration.AddTarget(loggerName + "LogFile", target);
                var ruleInfo = new LoggingRule(loggerName, NLog.LogLevel.Debug, target);
                LogManager.Configuration.LoggingRules.Add(ruleInfo);
                LogManager.ReconfigExistingLoggers();
            }

            TimeSource.Current = new AccurateUtcTimeSource();
            OLogger = LogManager.GetLogger(loggerName);

            // FileTarget settings are not preserved across FH deployments, so try and clean old files on FH start up. \\

            if (string.IsNullOrWhiteSpace(FolderName))
            {
                return;
            }

            string folder = Path.Combine(logFolderBase, FolderName);

            if (MaxArchiveFileLifetimeDays > 0)
            {
                TryCleanFolder(folder, "*.log", TimeSpan.FromDays(MaxArchiveFileLifetimeDays));
            }
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

        /// <summary>
        /// Logs EventSource events as specified event name using T data as payload.
        /// </summary>
        /// <typeparam name="T">Generic type. Must be a class or struct attributed as EventData (EventSource.EventDataAttribute).</typeparam>
        /// <param name="eventName">The name of the ETW event. This corresponds to the table name in Kusto.</param>
        /// <param name="eventData">The data of generic type that will be the event Payload.</param>
        public static void LogEtw<T>(string eventName, T eventData)
        {
            if (eventData == null || string.IsNullOrWhiteSpace(eventName))
            {
                return;
            }

            if (!JsonSerializationUtility.TrySerializeObject(eventData, out string data))
            {
                return;
            }

            EventKeywords keywords = ServiceEventSource.Keywords.InternalData;

            if (eventData is TelemetryData telemData)
            {
                if (telemData.HealthState == HealthState.Error || telemData.HealthState == HealthState.Warning)
                {
                    keywords = ServiceEventSource.Keywords.ErrorOrWarning;
                }
            }

            ServiceEventSource.Current.Write(new { data }, eventName, keywords);
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

        public void TryCleanFolder(string folderPath, string searchPattern, TimeSpan maxAge)
        {
            if (!Directory.Exists(folderPath))
            {
                return;
            }

            string[] files = Array.Empty<string>();

            try
            {
                files = Directory.GetFiles(folderPath, searchPattern, SearchOption.AllDirectories);
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException)
            {
                return;
            }

            foreach (string file in files)
            {
                try
                {
                    if (DateTime.UtcNow.Subtract(File.GetCreationTime(file)) >= maxAge)
                    {
                        Retry.Do(() => File.Delete(file), TimeSpan.FromSeconds(1), CancellationToken.None);
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is AggregateException)
                {
                    LogWarning($"Unable to delete file {file}:{Environment.NewLine}{e.Message}");
                }
            }
        }
    }
}