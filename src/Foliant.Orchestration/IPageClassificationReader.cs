using ZD = ZeroDep.Abstractions;

namespace Foliant.Orchestration;

/// <summary>
/// Reads ZeroDep's per-page structural classification into the orchestrator's engine-agnostic
/// <see cref="PageRoutingInput"/> vocabulary — the single seam that binds ZeroDep types. Keeping every
/// ZeroDep reference behind this interface means an engine API change is absorbed in one place and never
/// reaches <see cref="RoutingPolicy"/>.
/// </summary>
public interface IPageClassificationReader
{
    /// <summary>Map a ZeroDep <see cref="ZD.DocumentAnalysis"/> to per-page routing inputs, in page order.</summary>
    IReadOnlyList<PageRoutingInput> Read(ZD.DocumentAnalysis analysis);
}
