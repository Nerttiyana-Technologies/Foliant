using System.ComponentModel;
using System.Reflection;
using Foliant.Mcp.Extraction;
using Foliant.Mcp.Shaping;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Foliant.Mcp.Tools;

/// <summary>Configuration diagnosis without triggering heavy work (ADR-0005 D5/D10).</summary>
[McpServerToolType]
public static class HealthTools
{
    [McpServerTool(Name = "server_health"),
     Description(
        "Report this server's state without loading anything heavy: version, models directory and " +
        "whether it exists, processor state (not-loaded/loading/ready/failed), template mode, " +
        "extraction run counts, page caps, and the privacy gate. Call this first when a tool " +
        "reports missing models or unexpected configuration.")]
    public static string ServerHealth(
        ProcessorHolder processor,
        TemplateRouterHolder templates,
        ExtractionRunRegistry registry,
        IOptions<FoliantMcpOptions> options,
        IOptions<PrivacyOptions> privacy)
    {
        var opts = options.Value;
        var (active, total) = registry.Counts();

        string version =
            Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";

        bool modelsDirSet = !string.IsNullOrWhiteSpace(opts.ModelsDir);

        return Shape.ToJson(new
        {
            server = "Foliant.Mcp",
            version,
            processorState = processor.State,
            models = new
            {
                modelsDir = modelsDirSet ? opts.ModelsDir : null,
                modelsDirExists = modelsDirSet && Directory.Exists(opts.ModelsDir),
                note = modelsDirSet
                    ? null
                    : "Foliant__ModelsDir not set — first extraction downloads ~330 MB into the " +
                      "verified model cache.",
            },
            templates = templates.Mode,
            zeroDepFastLane = opts.UseZeroDepFastLane,
            limits = new
            {
                maxPagesPerRun = opts.MaxPages,
                summarySyncPageLimit = opts.SummarySyncPageLimit,
                resultWindowMaxPages = Shape.MaxWindowPages,
            },
            runs = new { active, total },
            privacy = new { blockSensitivePages = privacy.Value.BlockSensitivePages },
        });
    }
}
