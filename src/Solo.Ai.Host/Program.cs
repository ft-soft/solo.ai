using Solo.Ai.Api;

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Configuration.AddJsonFile("appsettings.Secrets.json", optional: true).AddEnvironmentVariables();
    builder.Logging.AddSoloAiSafeLogging();
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
    builder.Services.AddSoloAiAuthentication(builder.Configuration, builder.Environment);
    // Storage initialization and the process lock must precede generation recovery/dispatch.
    builder.Services.AddSoloAiStorage(builder.Configuration);
    builder.Services.AddSoloAiApi(builder.Configuration);
    await using var app = builder.Build();
    app.UseMiddleware<SoloAiExceptionMiddleware>();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseMiddleware<SoloAiRequestMiddleware>();
    app.MapSoloAiChatEndpoints();
    SoloAiHealth.MapEndpoints(app);
    await app.RunAsync();
}
catch (Exception)
{
    // Binding/SQLite/auth exceptions may contain configuration values or paths.
    Console.Error.WriteLine("Solo AI stopped with host_failure. Check local configuration, storage and the single-process lock.");
    Environment.ExitCode = 1;
}
