using Foliant.Mcp.Extraction;
using Xunit;

namespace Foliant.Mcp.Tests;

public class ExtractionRunRegistryTests
{
    private static DocumentResult EmptyResult() =>
        new(Array.Empty<PageResult>(), string.Empty);

    [Fact]
    public void Create_StartsPending_AndIsRetrievable()
    {
        var registry = new ExtractionRunRegistry();

        var run = registry.Create("/tmp/a.pdf", pageCount: 12);

        Assert.Equal(ExtractionRunStatus.Pending, run.Status);
        Assert.Equal(12, run.PageCount);
        Assert.Equal(0, run.PagesDone);
        Assert.Same(run, registry.Get(run.Id));
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        var registry = new ExtractionRunRegistry();

        Assert.Null(registry.Get("nope"));
    }

    [Fact]
    public void Lifecycle_PendingToRunningToCompleted()
    {
        var registry = new ExtractionRunRegistry();
        var run = registry.Create("/tmp/a.pdf", 3);

        run.MarkRunning();
        Assert.Equal(ExtractionRunStatus.Running, run.Status);
        Assert.Null(run.CompletedAtUtc);

        run.ReportPagesDone(2);
        Assert.Equal(2, run.PagesDone);

        run.MarkCompleted(EmptyResult());
        Assert.Equal(ExtractionRunStatus.Completed, run.Status);
        Assert.NotNull(run.Result);
        Assert.NotNull(run.CompletedAtUtc);
    }

    [Fact]
    public void MarkFailed_RecordsError_AndCompletionTime()
    {
        var registry = new ExtractionRunRegistry();
        var run = registry.Create("/tmp/a.pdf", 3);

        run.MarkRunning();
        run.MarkFailed("boom");

        Assert.Equal(ExtractionRunStatus.Failed, run.Status);
        Assert.Equal("boom", run.Error);
        Assert.NotNull(run.CompletedAtUtc);
    }

    [Fact]
    public void Counts_TracksActiveAndTotal()
    {
        var registry = new ExtractionRunRegistry();
        var a = registry.Create("/tmp/a.pdf", 1);
        var b = registry.Create("/tmp/b.pdf", 1);
        registry.Create("/tmp/c.pdf", 1);   // stays pending

        a.MarkRunning();
        b.MarkRunning();
        b.MarkCompleted(EmptyResult());

        var (active, total) = registry.Counts();
        Assert.Equal(2, active);   // a (running) + c (pending)
        Assert.Equal(3, total);
    }

    [Fact]
    public void RunIds_AreUnique()
    {
        var registry = new ExtractionRunRegistry();
        var ids = Enumerable.Range(0, 50).Select(_ => registry.Create("/tmp/x.pdf", 1).Id).ToHashSet();

        Assert.Equal(50, ids.Count);
    }
}
