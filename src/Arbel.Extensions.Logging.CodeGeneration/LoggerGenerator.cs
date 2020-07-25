using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arbel.Extensions.Logging.CodeGeneration
{
    internal class LoggerGenerator
    {
        private readonly LoggerTypeGenerator _typeGenerator;
        private readonly Delegate[]? _configurationDelegates;
        private readonly LoggerGeneratorOptions _options;
        private readonly ILoggerFactory _loggerFactory;

        public LoggerGenerator(IOptions<LoggerGeneratorOptions> options, ILoggerFactory loggerFactory)
        {
            _options = options.Value;
            _loggerFactory = loggerFactory;
            _configurationDelegates = _options.ToDelegateArray();
            _typeGenerator = new LoggerTypeGenerator(_options);
        }

        public T Generate<T>()
        {
            return (T)Generate(typeof(T));
        }

        public object Generate(Type interfaceType)
        {
            var generatedType = _typeGenerator.GenerateType(interfaceType);
            return Activator.CreateInstance(generatedType, _loggerFactory.CreateLogger(GetCategoryName()),
                _options.FallbackConverter, _configurationDelegates);

            string GetCategoryName()
            {
                var loggerCategoryAttribute = interfaceType.GetCustomAttribute<LoggerCategoryAttribute>();
                return !string.IsNullOrEmpty(loggerCategoryAttribute?.Name)
                    ? loggerCategoryAttribute!.Name!
                    : GetNameFromType();
            }

            string GetNameFromType()
            {
                var name = interfaceType.Name;
                if (interfaceType.IsGenericType)
                {
                    name += "-" + string.Join("-", interfaceType.GenericTypeArguments.Select(x => x.Name));
                }

                return name;
            }
        }
    }
}
