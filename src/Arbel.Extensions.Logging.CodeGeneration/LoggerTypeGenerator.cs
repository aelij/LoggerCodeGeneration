using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.Logging;

namespace Arbel.Extensions.Logging.CodeGeneration
{
    internal sealed class LoggerTypeGenerator
    {
        private const string AssemblyName = "GeneratedLoggers";

        private static readonly MethodInfo s_isEnabledMethodInfo = typeof(ILogger).GetMethod(nameof(ILogger.IsEnabled));
        private static readonly MethodInfo s_logMethodInfo = typeof(LoggerExtensions).GetMethod(nameof(LoggerExtensions.Log),
            new[] { typeof(ILogger), typeof(LogLevel), typeof(EventId), typeof(Exception), typeof(string), typeof(object[]) });

        private static readonly ImmutableArray<MethodInfo> s_loggerMessageMethods = typeof(LoggerMessage)
            .GetMethods().Where(m => m.Name == nameof(LoggerMessage.Define) && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(LogLevel), typeof(EventId), typeof(string) }))
            .OrderBy(m => m.IsGenericMethod ? m.GetGenericArguments().Length : 0)
            .ToImmutableArray();

        private static readonly ConstructorInfo s_eventIdCtor = typeof(EventId).GetConstructor(new[] { typeof(int), typeof(string) });

        private readonly AssemblyBuilder _assembly;
        private readonly ModuleBuilder _module;
        private readonly ConcurrentDictionary<Type, Type> _typeCache;
        private readonly LoggerGeneratorOptions _options;

        public LoggerTypeGenerator(LoggerGeneratorOptions options)
        {
            _options = options;
            const string moduleName = AssemblyName + ".dll";
            _assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(AssemblyName), AssemblyBuilderAccess.Run);
            _module = _assembly.DefineDynamicModule(moduleName);
            _typeCache = new ConcurrentDictionary<Type, Type>();
        }

        public Type GenerateType(Type interfaceType)
        {
            return _typeCache.GetOrAdd(interfaceType, _ => new TypeGenerator(this, interfaceType).GeneratedType);
        }

        private sealed class TypeGenerator
        {
            private readonly LoggerTypeGenerator _generator;
            private readonly Type _interfaceType;
            private readonly LoggerGeneratorOptions _options;

            private readonly TypeBuilder _type;
            private readonly FieldBuilder _loggerField;
            private readonly FieldBuilder _convertersField;
            private readonly FieldBuilder _fallbackConverterField;
            private readonly MethodBuilder _fallbackConverterGetter;
            private readonly Dictionary<MethodInfo, FieldBuilder> _loggerMessageFields;

            public Type GeneratedType { get; }

            public TypeGenerator(LoggerTypeGenerator generator, Type interfaceType)
            {
                _generator = generator;
                _interfaceType = interfaceType;
                _options = _generator._options;
                _loggerMessageFields = new Dictionary<MethodInfo, FieldBuilder>();

                var name = _interfaceType.Name;
                if (name.StartsWith("I", StringComparison.Ordinal)) name = name.Substring(1);

                _type = _generator._module.DefineType(_interfaceType.Namespace + "." + name, TypeAttributes.Public,
                    parent: typeof(object), new[] { _interfaceType });

                _loggerField = _type.DefineField("_logger", typeof(ILogger), FieldAttributes.Private | FieldAttributes.InitOnly);
                _convertersField = _type.DefineField("_converters", typeof(Delegate[]), FieldAttributes.Private | FieldAttributes.InitOnly);

                (_fallbackConverterField, _fallbackConverterGetter) = DefineFallbackConverter();

                var methods = GetDeclaredMembers(_interfaceType).Select(GetMethodData).ToArray();

                GenerateConstructor(methods);

                foreach (var method in methods)
                {
                    GenerateMethod(method);
                }

                GeneratedType = _type.CreateTypeInfo()!.AsType();
            }

            private MethodData GetMethodData(MemberInfo memberInfo)
            {
                var methodInfo = memberInfo as MethodInfo;
                if (methodInfo == null)
                {
                    throw new InvalidOperationException($"Only methods can be defined ({memberInfo})");
                }

                if (methodInfo.ReturnType != typeof(void))
                {
                    throw new InvalidOperationException($"Only void-returning methods are allowed ({methodInfo})");
                }

                var eventAttribute = methodInfo.GetCustomAttribute<LoggerEventAttribute>() ??
                    new LoggerEventAttribute();

                var methodData = new MethodData(methodInfo, eventAttribute, GetParameterData(methodInfo));
                return methodData;
            }

            private static IEnumerable<MemberInfo> GetDeclaredMembers(Type type)
            {
                return type.GetTypeInfo().ImplementedInterfaces.Reverse()
                    .Concat(new[] { type })
                    .SelectMany(c => c.GetTypeInfo().DeclaredMembers);
            }

            private (FieldBuilder, MethodBuilder) DefineFallbackConverter()
            {
                var fallbackConverterField = _type.DefineField("_fallbackConverter", typeof(Func<object, string>),
                    FieldAttributes.Private | FieldAttributes.InitOnly);

                var converterProperty = _type.DefineProperty("FallbackConverter", PropertyAttributes.None,
                    fallbackConverterField.FieldType, Array.Empty<Type>());

                var fallbackConverterGetter = _type.DefineMethod("get_" + converterProperty.Name,
                    MethodAttributes.Private | MethodAttributes.SpecialName |
                    MethodAttributes.HideBySig, converterProperty.PropertyType, Array.Empty<Type>());

                var il = fallbackConverterGetter.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, fallbackConverterField);
                var label = il.DefineLabel();
                il.Emit(OpCodes.Brtrue_S, label);
                il.Emit(OpCodes.Ldstr, "Fallback converter missing");
                il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) }));
                il.Emit(OpCodes.Throw);
                il.MarkLabel(label);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, fallbackConverterField);
                il.Emit(OpCodes.Ret);

                converterProperty.SetGetMethod(fallbackConverterGetter);

                return (fallbackConverterField, fallbackConverterGetter);
            }

            private void GenerateConstructor(MethodData[] methods)
            {
                var constructor = _type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard,
                    new[] { _loggerField.FieldType, _fallbackConverterField.FieldType, _convertersField.FieldType });
                constructor.DefineParameter(1, ParameterAttributes.None, _fallbackConverterField.Name.TrimStart('_'));
                var il = constructor.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, _type.BaseType!.GetConstructor(Array.Empty<Type>()));
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, _loggerField);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Stfld, _fallbackConverterField);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Stfld, _convertersField);

                var methodIndex = 0;
                foreach (var method in methods)
                {
                    TryEmitLoggerMessage(method);
                }

                il.Emit(OpCodes.Ret);

                void TryEmitLoggerMessage(MethodData method)
                {
                    if (method.Parameters.Length >= s_loggerMessageMethods.Length)
                    {
                        return;
                    }

                    var defineMethod = s_loggerMessageMethods[method.Parameters.Length];
                    if (method.Parameters.Length > 0)
                    {
                        defineMethod = defineMethod.MakeGenericMethod(method.Parameters.Select(p => p.FinalType).ToArray());
                    }

                    var field = _type.DefineField($"_{method.MethodInfo.Name}{methodIndex++}", defineMethod.ReturnType, FieldAttributes.Private | FieldAttributes.InitOnly);
                    _loggerMessageFields.Add(method.MethodInfo, field);
                    il.Emit(OpCodes.Ldarg_0);
                    // LoggerMessage.Define(method.Attribute.Level, new EventId(0, method.MethodInfo.Name), GetFormatString(method));
                    EmitLogLevelAndEventId(method, il);
                    EmitFormatString(method, il);
                    il.Emit(OpCodes.Call, defineMethod);
                    il.Emit(OpCodes.Stfld, field);
                }
            }

            private static void EmitLogLevelAndEventId(MethodData method, ILGenerator il)
            {
                il.Emit(OpCodes.Ldc_I4, (int)method.Attribute.Level);
                il.Emit(OpCodes.Ldc_I4, 0);
                il.Emit(OpCodes.Ldstr, method.MethodInfo.Name);
                il.Emit(OpCodes.Newobj, s_eventIdCtor);
            }

            private void EmitFormatString(MethodData method, ILGenerator il)
            {
                il.Emit(OpCodes.Ldstr, method.Attribute.Format ?? GetFormatString());

                string GetFormatString()
                {
                    return string.Join(", ", method.Parameters.Select(p =>
                    {
                        var name = _options.ParameterNameFormatter?.Invoke(p.Parameter.Name) ?? p.Parameter.Name;
                        return $"{name}={{{name}}}";
                    }));
                }
            }

            private void GenerateMethod(MethodData method)
            {
                GenerateInterfaceMethod(method);
            }

            private ParameterData[] GetParameterData(MethodInfo sourceMethodInfo)
            {
                return sourceMethodInfo.GetParameters().Select(GetParameterData).ToArray();

                ParameterData GetParameterData(ParameterInfo parameterInfo)
                {
                    var converter = _options.TryGetConverter(parameterInfo.ParameterType);
                    if (converter == null)
                    {
                        return new ParameterData(parameterInfo);
                    }
                    else
                    {
                        return new ParameterData(parameterInfo, converter);
                    }
                }
            }

            private void GenerateInterfaceMethod(MethodData method)
            {
                var paramters = method.MethodInfo.GetParameters().Select(x => x.ParameterType).ToArray();

                var methodBuilder = _type.DefineMethod(method.MethodInfo.Name,
                    MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.HideBySig |
                    MethodAttributes.NewSlot |
                    MethodAttributes.Virtual,
                    CallingConventions.HasThis, method.MethodInfo.ReturnType, paramters);

                var position = 1;
                foreach (var parameterInfo in method.MethodInfo.GetParameters())
                {
                    methodBuilder.DefineParameter(position++, ParameterAttributes.None, parameterInfo.Name);
                }

                var il = methodBuilder.GetILGenerator();

                EmitIsEnabled();

                if (_loggerMessageFields.TryGetValue(method.MethodInfo, out var loggerMessageField))
                {
                    EmitLoggerMessageCall(loggerMessageField);
                }
                else
                {
                    EmitLoggerCall();
                }

                il.Emit(OpCodes.Ret);

                void EmitIsEnabled()
                {
                    // if (!IsEnabled(level, keyword)) return
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, _loggerField);
                    il.Emit(OpCodes.Ldc_I4, (int)method.Attribute.Level);
                    il.Emit(OpCodes.Callvirt, s_isEnabledMethodInfo);
                    var enabledLabel = il.DefineLabel();
                    il.Emit(OpCodes.Brtrue, enabledLabel);
                    il.Emit(OpCodes.Ret);
                    il.MarkLabel(enabledLabel);
                }

                void EmitParameter(ParameterData p)
                {
                    // ((Func)_converters[index]).Invoke()
                    if (p.ConverterIndex != null)
                    {
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldfld, _convertersField);
                        il.Emit(OpCodes.Ldc_I4, p.ConverterIndex.Value);
                        il.Emit(OpCodes.Ldelem_Ref);
                        il.Emit(OpCodes.Castclass, p.ConverterDelegate!.GetType());
                        il.Emit(OpCodes.Ldarg, p.Parameter.Position + 1);
                        il.Emit(OpCodes.Callvirt, p.ConverterDelegate.GetType().GetRuntimeMethod("Invoke", new[] { p.Parameter.ParameterType }));
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldarg, p.Parameter.Position + 1);
                    }

                    if (!p.RequiresFallback)
                    {
                        // FallbackConverter(value)
                        var local = il.DeclareLocal(p.Type);
                        il.Emit(OpCodes.Stloc, local);
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Call, _fallbackConverterGetter);
                        il.Emit(OpCodes.Ldloc, local);
                        if (local.LocalType.IsValueType)
                        {
                            il.Emit(OpCodes.Box, local.LocalType);
                        }

                        il.Emit(OpCodes.Callvirt, _fallbackConverterGetter.ReturnType.GetRuntimeMethod("Invoke", new[] { typeof(object) }));
                    }
                }

                void EmitLoggerMessageCall(FieldBuilder loggerMessageField)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, loggerMessageField);

                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, _loggerField);

                    foreach (var p in method.Parameters)
                    {
                        EmitParameter(p);
                    }

                    EmitException();

                    il.Emit(OpCodes.Callvirt, loggerMessageField.FieldType.GetMethod("Invoke"));
                }

                void EmitLoggerCall()
                {
                    // LoggerExtensions.Log(_logger, logLevel, eventId, Exception exception, format, arg1, arg2, ...);

                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, _loggerField);
                    EmitLogLevelAndEventId(method, il);
                    EmitException();
                    EmitFormatString(method, il);

                    if (method.Parameters.Length > 0)
                    {
                        il.Emit(OpCodes.Ldc_I4, method.Parameters.Length);
                        il.Emit(OpCodes.Newarr, typeof(object));

                        for (var i = 0; i < method.Parameters.Length; ++i)
                        {
                            il.Emit(OpCodes.Dup);
                            il.Emit(OpCodes.Ldc_I4, i);
                            EmitParameter(method.Parameters[i]);
                            if (method.Parameters[i].FinalType.IsValueType)
                            {
                                il.Emit(OpCodes.Box, method.Parameters[i].FinalType);
                            }

                            il.Emit(OpCodes.Stelem, typeof(object));
                        }
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldnull);
                    }

                    il.Emit(OpCodes.Call, s_logMethodInfo);
                }

                void EmitException()
                {
                    if (method.ExceptionParameter != null)
                    {
                        il.Emit(OpCodes.Ldarg, method.ExceptionParameter.Parameter.Position + 1);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldnull);
                    }
                }
            }
        }

        private class MethodData
        {
            public MethodData(MethodInfo methodInfo, LoggerEventAttribute attribute, ParameterData[] parameters)
            {
                MethodInfo = methodInfo;
                Attribute = attribute;
                ExceptionParameter = parameters.LastOrDefault(p => typeof(Exception).IsAssignableFrom(p.Type));
                Parameters = ExceptionParameter == null ? parameters : parameters.Except(new[] { ExceptionParameter }).ToArray();
            }

            public MethodInfo MethodInfo { get; }
            public LoggerEventAttribute Attribute { get; }
            public ParameterData? ExceptionParameter { get; }
            public ParameterData[] Parameters { get; }
        }

        private class ParameterData
        {
            public ParameterData(ParameterInfo parameter, Converter? converter = null)
            {
                Parameter = parameter;
                ConverterIndex = converter?.Index;
                ConverterDelegate = converter?.Func;
                Type = ConverterDelegate?.GetMethodInfo().ReturnType ?? Parameter.ParameterType;
                RequiresFallback = GetRequiresFallback(Type);
                FinalType = RequiresFallback ? Type : typeof(string);

            }

            public ParameterInfo Parameter { get; }
            public int? ConverterIndex { get; }
            public Delegate? ConverterDelegate { get; }
            public Type Type { get; }
            public bool RequiresFallback { get; }
            public Type FinalType { get; }

            private static bool GetRequiresFallback(Type type)
            {
                if (type.IsPrimitive ||
                    type.IsEnum ||
                    type == typeof(string) ||
                    type == typeof(DateTime) ||
                    type == typeof(DateTimeOffset) ||
                    type == typeof(TimeSpan) ||
                    type == typeof(Guid) ||
                    (Nullable.GetUnderlyingType(type) is Type nullable && GetRequiresFallback(nullable)) ||
                    (type.FindInterfaces((t, _) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>), null).FirstOrDefault() is Type enumerable &&
                        GetRequiresFallback(enumerable.GetGenericArguments()[0])) ||
                    (typeof(System.Collections.IStructuralEquatable).IsAssignableFrom(type) && type.IsGenericType && type.GetGenericArguments().All(GetRequiresFallback)))
                {
                    return true;
                }

                return false;
            }
        }
    }
}