using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Solo.Ai.Api;

public static class SoloAiLoggingExtensions
{
    public static ILoggingBuilder AddSoloAiSafeLogging(this ILoggingBuilder logging)
    {
        // Host policy: configuration cannot re-enable framework HTTP/auth/exception payload logging.
        logging.Services.PostConfigure<LoggerFilterOptions>(options =>
        {
            options.Rules.Clear();
            options.Rules.Add(new(null, null, null, (_, category, level) =>
                level >= LogLevel.Information && (category == typeof(SoloAiRunExecutor).FullName ||
                    category == typeof(SoloAiGenerationWorker).FullName ||
                    category == typeof(SoloAiStorageWorker).FullName ||
                    category == typeof(SoloAiExceptionMiddleware).FullName)));
        });
        return logging;
    }
}
