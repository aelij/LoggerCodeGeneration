using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

#pragma warning disable IDE1006 // Naming Styles

namespace Arbel.Extensions.Logging.CodeGeneration.Tests
{
    public class LoggerGeneratorTest
    {
        private readonly TestLogger _testLogger = new TestLogger();

        [Theory]
        [MemberData(nameof(SimpleDataTypesParameters))]
        public void SupportSimpleDataTypes(Type type, object o, object expected)
        {
            var generator = CreateGenerator();
            var genericType = typeof(ITestLogData<>).MakeGenericType(type);
            var logger = generator.Generate(genericType);
            var testMethod = genericType.GetTypeInfo().GetMethod("Test");
            testMethod!.Invoke(logger, new[] { o });
            Assert.Equal(expected, _testLogger.State.Values.First());
        }

        public static IEnumerable<object?[]> SimpleDataTypesParameters
        {
            get
            {
                yield return new object?[] { typeof(int), default(int), default(int) };
                yield return new object?[] { typeof(string), default(string), default(string) };
                yield return new object?[] { typeof(string), string.Empty, string.Empty };
                yield return new object?[] { typeof(string), "Test", "Test" };
                yield return new object?[] { typeof(DayOfWeek), DayOfWeek.Sunday, DayOfWeek.Sunday };
                yield return new object?[] { typeof(byte[]), new[] { (byte)1 }, new[] { (byte)1 } };
                yield return new object?[] { typeof(ValueTuple<int, int>), (1, 1), (1, 1) };
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        public void MultipleParameters(int count)
        {
            var generator = CreateGenerator();
            var logger = generator.Generate<ILoggerWithParameters>();
            var testMethod = typeof(ILoggerWithParameters).GetMethod("Test", Enumerable.Repeat(typeof(int), count).ToArray());
            var args = Enumerable.Range(0, count).Cast<object>().ToArray();
            testMethod!.Invoke(logger, args);
            Assert.Equal(args, _testLogger.State.Values);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        public void MultipleParametersWithException(int count)
        {
            var generator = CreateGenerator();
            var logger = generator.Generate<ILoggerWithParameters>();
            var testMethod = typeof(ILoggerWithParameters).GetMethod("TestException",
                Enumerable.Repeat(typeof(int), count - 1).Prepend(typeof(Exception)).Prepend(typeof(int)).ToArray());
            var exception = new Exception();
            var args = Enumerable.Range(1, count - 1).Cast<object>().Prepend(exception).Prepend(0).ToArray();
            testMethod!.Invoke(logger, args);
            Assert.Same(exception, _testLogger.Exception);
            Assert.Equal(args.Except(new[] { exception }), _testLogger.State.Values);
        }

        [Fact]
        public void CategoryName()
        {
            var generator = CreateGenerator();
            generator.Generate<ITestLogDerived>();
            Assert.Equal("Arbel.Extensions.Logging.CodeGeneration.Tests.TestLogDerived", _testLogger.CategoryName);
        }

        [Fact]
        public void EventIdWithoutAttribute()
        {
            var expected = 1;
            var generator = CreateGenerator();
            var logger = generator.Generate<ITestLogNoAttributes>();
            logger.Test(expected);
            Assert.Equal(expected, _testLogger.State.Values.First());
        }

        [Fact]
        public void EventDifferentLevel()
        {
            var generator = CreateGenerator();
            var logger = generator.Generate<ITestLevelWarning>();
            logger.Test();
            Assert.Equal(LogLevel.Warning, _testLogger.LogLevel);
        }

        [Fact]
        public void EventCustomFormat()
        {
            var generator = CreateGenerator();
            var logger = generator.Generate<ITestLevelCustomFormat>();
            logger.Test(1, "2");
            Assert.Equal("Custom12", _testLogger.Message);
        }

        [Fact]
        public void DerivedInterfaces()
        {
            var expected = 1;
            var generator = CreateGenerator();
            var logger = generator.Generate<ITestLogDerived>();
            logger.Derived(expected);
            Assert.Equal(expected, _testLogger.State.Values.First());
        }

        [Fact]
        public void DerivedInterfacesBaseCall()
        {
            var expected = 1;
            var generator = CreateGenerator();
            var logger = generator.Generate<ITestLogDerived>();
            logger.Base(expected);
            Assert.Equal(expected, _testLogger.State.Values.First());
        }

        [Fact]
        public void CustomDataNoFallback_Throws()
        {
            var generator = CreateGenerator();
            var logger = generator.Generate<ITestLogData<CustomData>>();
            Assert.Throws<InvalidOperationException>(() => logger.Test(new CustomData()));
        }

        [Fact]
        public void CustomDataToStringFallback()
        {
            var generator = CreateGenerator(useToStringFallback: true);
            var logger = generator.Generate<ITestLogData<CustomData>>();
            logger.Test(new CustomData());
            Assert.Equal(nameof(CustomData), _testLogger.State.Values.First());
        }

        [Fact]
        public void CustomDataMapping()
        {
            var generator = CreateGenerator(false, new LoggerGeneratorOptions().AddConverter((CustomData m) => 1));
            var logger = generator.Generate<ITestLogData<CustomData?>>();
            logger.Test2(null, 0);
            Assert.Equal(new object[] { 1, 0 }, _testLogger.State.Values);
        }

        private LoggerGenerator CreateGenerator(bool useToStringFallback = false, LoggerGeneratorOptions? options = null)
        {
            options ??= new LoggerGeneratorOptions();
            if (!useToStringFallback)
            {
                options.FallbackConverter = null;
            }

            return new LoggerGenerator(Options.Create(options), new TestLoggerFactory(_testLogger));
        }

        private class TestLoggerFactory : ILoggerFactory
        {
            private readonly TestLogger _testLogger;

            public TestLoggerFactory(TestLogger testLogger)
            {
                _testLogger = testLogger;
            }

            public void AddProvider(ILoggerProvider provider) { }

            public ILogger CreateLogger(string categoryName)
            {
                _testLogger.CategoryName = categoryName;
                return _testLogger;
            }

            public void Dispose() { }
        }

        private class TestLogger : ILogger
        {
            private Dictionary<string, object>? _state;

            public LogLevel LogLevel { get; private set; }
            public EventId EventId { get; private set; }
            public Exception? Exception { get; private set; }
            public Dictionary<string, object> State
            {
                get
                {
                    Assert.NotNull(_state);
                    return _state!;
                }

                private set => _state = value;
            }

            public string? Message { get; private set; }
            public string? CategoryName { get; internal set; }

            public IDisposable BeginScope<TState>(TState state) => throw new NotSupportedException();

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                LogLevel = logLevel;
                EventId = eventId;
                Exception = exception;
                if (state is IReadOnlyList<KeyValuePair<string, object>> list)
                {
                    State = list.Where(item => item.Key != "{OriginalFormat}").ToDictionary(item => item.Key, item => item.Value);
                }
                Message = formatter(state, exception);
            }
        }

        public interface ITestLogNoAttributes
        {
            void Test(int value);
        }

        public interface ITestLevelWarning
        {
            [LoggerEvent(Level = LogLevel.Warning)]
            void Test();
        }

        public interface ITestLevelCustomFormat
        {
            [LoggerEvent(Format = "Custom{a}{b}")]
            void Test(int a, string b);
        }

        public interface ITestLogBase
        {
            void Base(int value);
        }

        public interface ITestLogDerived : ITestLogBase
        {
            void Derived(int value);
        }

        public interface ILoggerWithParameters
        {
            void Test(int p1);
            void Test(int p1, int p2);
            void Test(int p1, int p2, int p3);
            void Test(int p1, int p2, int p3, int p4);
            void Test(int p1, int p2, int p3, int p4, int p5);
            void Test(int p1, int p2, int p3, int p4, int p5, int p6);
            void Test(int p1, int p2, int p3, int p4, int p5, int p6, int p7);
            void Test(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8);

            void TestException(int p1, Exception ex);
            void TestException(int p1, Exception ex, int p2);
            void TestException(int p1, Exception ex, int p2, int p3);
            void TestException(int p1, Exception ex, int p2, int p3, int p4);
            void TestException(int p1, Exception ex, int p2, int p3, int p4, int p5);
            void TestException(int p1, Exception ex, int p2, int p3, int p4, int p5, int p6);
            void TestException(int p1, Exception ex, int p2, int p3, int p4, int p5, int p6, int p7);
            void TestException(int p1, Exception ex, int p2, int p3, int p4, int p5, int p6, int p7, int p8);
        }

        [LoggerCategory(Name = "TestLogData")]
        public interface ITestLogData<in T>
        {
            void Test(T data);

            void Test2(T data, int i);
        }

        public class CustomData
        {
            public override string ToString()
            {
                return nameof(CustomData);
            }
        }
    }
}