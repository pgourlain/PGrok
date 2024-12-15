using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Console;

namespace PGrok.Services
{
    public sealed class CustomConsoleFormatter : ConsoleFormatter
    {
        public CustomConsoleFormatter() : base("pgrok") { }

        public override void Write<TState>(
            in LogEntry<TState> logEntry,
            IExternalScopeProvider? scopeProvider,
            TextWriter textWriter)
        {
            string? message = logEntry.Formatter?.Invoke(
                logEntry.State, logEntry.Exception);

            if (message is null)
            {
                return;
            }
            var originalColor = Console.ForegroundColor;
            var timestamp = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ";
            var logLevel = logEntry.LogLevel.ToString().ToUpper();
            var logLevelBrackets = $"[{logLevel}] ";

            textWriter.Write(timestamp);

            // Set color based on log level
            Console.ForegroundColor = GetLogLevelColor(logEntry.LogLevel);
            textWriter.Write(logLevelBrackets);
            textWriter.Write(message);
            textWriter.WriteLine();


            if (logEntry.Exception != null)
            {
                textWriter.WriteLine(logEntry.Exception.ToString());
            }
        }
        private static ConsoleColor GetLogLevelColor(LogLevel logLevel)
        {
            return logLevel switch {
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Error or LogLevel.Critical => ConsoleColor.Red,
                _ => ConsoleColor.Gray
            };
        }
    }
}
