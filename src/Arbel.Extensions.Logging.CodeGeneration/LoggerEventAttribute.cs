using System;
using Microsoft.Extensions.Logging;

namespace Arbel.Extensions.Logging.CodeGeneration
{
    [AttributeUsage(AttributeTargets.Method)]
    public class LoggerEventAttribute : Attribute
    {
        public LogLevel Level { get; set; } = LogLevel.Information;

        public string? Format { get; set; }
    }
}