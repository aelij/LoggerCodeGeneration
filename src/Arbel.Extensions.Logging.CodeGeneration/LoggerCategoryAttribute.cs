using System;

namespace Arbel.Extensions.Logging.CodeGeneration
{
    [AttributeUsage(AttributeTargets.Interface)]
    public class LoggerCategoryAttribute : Attribute
    {
        public string? Name { get; set; }
    }
}