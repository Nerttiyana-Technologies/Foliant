using System.Collections.Concurrent;

namespace Foliant.Mcp.Extraction;

public enum ExtractionRunStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
}

/// <summary>
/// One extraction run-ticket (ADR-0005 D7). Progress fields are written by the background task and
/// read by status tools; transitions are lock-guarded, the page counter is volatile.
/// </summary>
public sealed class ExtractionRun
{
    private readonly object _lock = new();
    private int _pagesDone;

    public required string Id { get; init; }
    public required string Path { get; init; }

    /// <summary>Pages this run will process (after the MaxPages cap), not the document's total.</summary>
    public required int PageCount { get; init; }

    public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public ExtractionRunStatus Status { get; private set; } = ExtractionRunStatus.Pending;
    public string? Error { get; private set; }
    public DocumentResult? Result { get; private set; }

    public int PagesDone => Volatile.Read(ref _pagesDone);
    public void ReportPagesDone(int completed) => Volatile.Write(ref _pagesDone, completed);

    public void MarkRunning()
    {
        lock (_lock)
        {
            if (Status == ExtractionRunStatus.Pending) Status = ExtractionRunStatus.Running;
        }
    }

    public void MarkCompleted(DocumentResult result)
    {
        lock (_lock)
        {
            Result = result;
            Status = ExtractionRunStatus.Completed;
            CompletedAtUtc = DateTimeOffset.UtcNow;
            Volatile.Write(ref _pagesDone, result.Pages.Count);
        }
    }

    public void MarkFailed(string error)
    {
        lock (_lock)
        {
            Error = error;
            Status = ExtractionRunStatus.Failed;
            CompletedAtUtc = DateTimeOffset.UtcNow;
        }
    }
}

/// <summary>
/// In-memory run registry. Sufficient by construction for a stdio server: the process lives exactly
/// as long as its one client session, so durable state would have no consumer (ADR-0005 D7). The
/// gate serializes heavy processor work — one extraction at a time; further requests queue.
/// </summary>
public sealed class ExtractionRunRegistry
{
    private readonly ConcurrentDictionary<string, ExtractionRun> _runs = new();

    /// <summary>Serializes ONNX-heavy work (extractions, form-field passes, sync summaries).</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public ExtractionRun Create(string path, int pageCount)
    {
        var run = new ExtractionRun
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Path = path,
            PageCount = pageCount,
        };
        _runs[run.Id] = run;
        return run;
    }

    public ExtractionRun? Get(string runId) =>
        _runs.TryGetValue(runId, out var run) ? run : null;

    public (int Active, int Total) Counts()
    {
        int active = _runs.Values.Count(r =>
            r.Status is ExtractionRunStatus.Pending or ExtractionRunStatus.Running);
        return (active, _runs.Count);
    }
}
