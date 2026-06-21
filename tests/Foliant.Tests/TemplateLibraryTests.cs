using System.Linq;
using Foliant;
using Foliant.Templates;
using Xunit;

namespace Foliant.Tests;

// BYO-template library: register a customer's own blank template, review/commit labels, route uploads.
// Uses ":memory:" — the TemplateLibrary holds one open SQLite connection for its lifetime, so the in-memory
// DB persists across calls on the same instance. FilledFormPdf has one /Tx widget (centre 0.5,0.2, /V value).
public sealed class TemplateLibraryTests
{
    [Fact]
    public void Register_MakesTemplateImmediatelyRoutable()
    {
        using var lib = new TemplateLibrary(":memory:");

        var draft = lib.Register(FilledFormPdf.Build(), "cust-form", "Customer Form");

        Assert.Equal("cust-form", draft.TemplateId);
        Assert.Contains(lib.AllTemplates(), t => t.TemplateId == "cust-form");
        var match = lib.Router.TryRoute(FilledFormPdf.Build(), 1);   // routable without rebuilding
        Assert.NotNull(match);
        Assert.Equal("cust-form", match!.TemplateId);
    }

    [Fact]
    public void Update_CommitsReviewedLabels()
    {
        using var lib = new TemplateLibrary(":memory:");
        var draft = lib.Register(FilledFormPdf.Build(), "cust-form", "Customer Form");

        var reviewed = draft with
        {
            Elements = draft.Elements.Select(e => e with { Label = "SOLICITATION NUMBER" }).ToList(),
        };
        lib.Update(reviewed);

        Assert.Equal("SOLICITATION NUMBER", lib.Get("cust-form")!.Elements[0].Label);
    }

    [Fact]
    public void ExportImport_RoundTripsReviewedLabelsViaJson()
    {
        using var lib = new TemplateLibrary(":memory:");
        var draft = lib.Register(FilledFormPdf.Build(), "cust-form", "Customer Form");

        // The offline review step: serialize → edit labels → parse → commit.
        string json = TemplateLibrary.ToJson(draft with
        {
            Elements = draft.Elements.Select(e => e with { Label = "REVIEWED" }).ToList(),
        });
        lib.Update(TemplateLibrary.FromJson(json));

        Assert.Equal("REVIEWED", lib.Get("cust-form")!.Elements[0].Label);
    }

    [Fact]
    public void Unregister_RemovesCustomerTemplate()
    {
        using var lib = new TemplateLibrary(":memory:");
        lib.Register(FilledFormPdf.Build(), "cust-form", "Customer Form");

        Assert.True(lib.Unregister("cust-form"));
        Assert.DoesNotContain(lib.CustomerTemplates(), t => t.TemplateId == "cust-form");
        Assert.Null(lib.Router.TryRoute(FilledFormPdf.Build(), 1));   // no longer routable → falls back
    }

    [Fact]
    public void CustomerTemplate_OverridesBundled_OnSameId()
    {
        using var lib = new TemplateLibrary(":memory:");

        // A customer template reusing a BUNDLED id must win in the merged candidate set.
        lib.Update(new FormLayout("SF1449-21", "Customer Override", new[]
        {
            new FormElement(FormElementKind.Text, 1, new NormalizedRect(0.1f, 0.1f, 0.9f, 0.3f), "X"),
        }));

        var resolved = lib.AllTemplates().Single(t => t.TemplateId == "SF1449-21");
        Assert.Equal("Customer Override", resolved.Name);
    }
}
