using System;
using System.Collections.Generic;
using System.Linq;

namespace Arbel.Extensions.Logging.CodeGeneration
{
    public class LoggerGeneratorOptions
    {
        private readonly Dictionary<Type, Converter> _converters;
        private int _converterIndex;

        private LoggerGeneratorOptions(Dictionary<Type, Converter> converters)
        {
            _converters = converters;
        }

        public LoggerGeneratorOptions() : this(new Dictionary<Type, Converter>())
        {
        }

        public Func<object, string?>? FallbackConverter { get; set; } = value => value?.ToString();

        public Func<string, string>? ParameterNameFormatter { get; set; }

        public LoggerGeneratorOptions AddConverter<T, TValue>(Func<T, TValue> converter)
        {
            _converters[typeof(T)] = new Converter(converter, _converterIndex++);
            return this;
        }

        internal Converter? TryGetConverter(Type type)
        {
            _converters.TryGetValue(type, out var converter);
            return converter;
        }

        internal Delegate[] ToDelegateArray()
        {
            return _converters.Select(x => x.Value)
                .OrderBy(x => x.Index)
                .Select(x => x.Func)
                .ToArray();
        }
    }
}