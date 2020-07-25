using Arbel.Extensions.Logging.CodeGeneration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class LoggerDependencyInjectionExtensions
    {
        public static IServiceCollection AddLogger<T>(this IServiceCollection services)
            where T : class
        {
            services.AddOptions<LoggerGeneratorOptions>();
            services.TryAddSingleton<LoggerGenerator>();
            services.AddSingleton(sp => sp.GetRequiredService<LoggerGenerator>().Generate<T>());
            return services;
        }
    }
}
