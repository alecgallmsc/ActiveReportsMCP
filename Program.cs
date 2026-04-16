using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RdlxMcpServer.Services;
using RdlxMcpServer.Tools;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ReportTools>();

builder.Services.AddSingleton<RdlxDocumentService>();
builder.Services.AddSingleton<RdlxValidationService>();
builder.Services.AddSingleton<RuntimeVerificationService>();
builder.Services.AddSingleton<LayoutIntelligenceService>();
builder.Services.AddSingleton<SchemaInspectionService>();
builder.Services.AddSingleton<SelfTestRunner>();

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(LogLevel.Information);

var host = builder.Build();

var runSelfTest = args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase));
var runSelfTestNoRuntime = args.Any(a => string.Equals(a, "--self-test-no-runtime", StringComparison.OrdinalIgnoreCase));

if (runSelfTest || runSelfTestNoRuntime)
{
    using var scope = host.Services.CreateScope();
    var runner = scope.ServiceProvider.GetRequiredService<SelfTestRunner>();
    var includeRuntimeChecks = runSelfTest && !runSelfTestNoRuntime;
    var exitCode = await runner.RunAsync(includeRuntimeChecks);
    Environment.ExitCode = exitCode;
    return;
}

await host.RunAsync();
