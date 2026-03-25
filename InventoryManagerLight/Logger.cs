using System;
using System.Diagnostics;

namespace InventoryManagerLight
{
    // Global static holder for runtime log level so components can check without passing config everywhere.
    public static class Log
    {
        public static RuntimeConfig.LogLevel CurrentLevel { get; set; } = RuntimeConfig.LogLevel.Info;
    }

    public interface ILogger
    {
        void Info(string msg);
        void Debug(string msg);
        void Warn(string msg);
        void Error(string msg);
        bool IsEnabled(RuntimeConfig.LogLevel level);
    }

    // Simple default logger that respects a min level
    public class DefaultLogger : ILogger
    {
        private readonly RuntimeConfig.LogLevel _minLevel;

        public DefaultLogger(RuntimeConfig.LogLevel minLevel = RuntimeConfig.LogLevel.Info)
        {
            _minLevel = minLevel;
        }

        public bool IsEnabled(RuntimeConfig.LogLevel level)
        {
            return level <= _minLevel;
        }

        private void Write(RuntimeConfig.LogLevel level, string prefix, string msg)
        {
            if (IsEnabled(level))
            {
                System.Diagnostics.Debug.WriteLine(prefix + ": " + msg);
            }
        }

        public void Info(string msg) => Write(RuntimeConfig.LogLevel.Info, "INFO", msg);
        public void Debug(string msg) => Write(RuntimeConfig.LogLevel.Debug, "DEBUG", msg);
        public void Warn(string msg) => Write(RuntimeConfig.LogLevel.Warn, "WARN", msg);
        public void Error(string msg) => Write(RuntimeConfig.LogLevel.Error, "ERROR", msg);
    }

#if TORCH
    // When running under Torch, use NLog via Torch's logging bridge if available.
    public class NLogLogger : ILogger
    {
        private readonly NLog.Logger _logger;
        private readonly RuntimeConfig.LogLevel _minLevel;

        public NLogLogger(RuntimeConfig.LogLevel minLevel = RuntimeConfig.LogLevel.Info)
        {
            _minLevel = minLevel;
            _logger = NLog.LogManager.GetLogger("InventoryManagerLight");
        }

        public bool IsEnabled(RuntimeConfig.LogLevel level)
        {
            return level <= _minLevel;
        }

        public void Info(string msg)
        {
            if (!IsEnabled(RuntimeConfig.LogLevel.Info)) return;
            _logger.Info(msg);
        }

        public void Debug(string msg)
        {
            if (!IsEnabled(RuntimeConfig.LogLevel.Debug)) return;
            _logger.Debug(msg);
        }

        public void Warn(string msg)
        {
            if (!IsEnabled(RuntimeConfig.LogLevel.Warn)) return;
            _logger.Warn(msg);
        }

        public void Error(string msg)
        {
            if (!IsEnabled(RuntimeConfig.LogLevel.Error)) return;
            _logger.Error(msg);
        }
    }
#endif
}
