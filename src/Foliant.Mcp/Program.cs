using Foliant.Mcp;
using Foliant.Mcp.Extraction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// appsettings.json ships next to the executable (dotnet tool). The default content-root probe uses
// the MCP client's working directory — which is arbitrary — so load it explicitly from the app base.
// Environment variables (Foliant__ModelsDir, Privacy__BlockSensitivePages, ...) override it, which is
// exactly what MCP client `env` blocks supply. ADR-0005 D8.
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);

// stdout is the JSON-RPC channel on stdio — every log line goes to stderr. A healthy server prints
// NOTHING to stdout. ADR-0005 D4.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.Configure<FoliantMcpOptions>(builder.Configuration.GetSection(FoliantMcpOptions.SectionName));
builder.Services.Configure<PrivacyOptions>(builder.Configuration.GetSection(PrivacyOptions.SectionName));

// Template router is model-free (fingerprint matching over PdfPig) — cheap, separate from the ONNX
// processor so match_template never pays the model-load cost. The processor holder builds lazily on
// the first extraction call (ADR-0005 D5): startup stays instant, tools/list never loads ONNX.
builder.Services.AddSingleton<TemplateRouterHolder>();
builder.Services.AddSingleton<ProcessorHolder>();
builder.Services.AddSingleton<ExtractionRunRegistry>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
