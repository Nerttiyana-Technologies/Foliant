using Foliant;
using Foliant.Templates;
using Xunit;

namespace Foliant.Tests;

// Registry-merge + per-page router tests. Reuses FilledFormPdf (one /Tx widget at normalized centre
// (0.5, 0.2), /V "ABC123-25-R-00001"). The negative-match test is the safety gate: a page whose widget
// signature differs must fall back, never bind wrong coordinates.
public sealed class TemplateRoutingTests
{
    private static FormLayout FormA() =>
        FormLayoutGenerator.Generate(FilledFormPdf.Build(), "form-a", "Form A");

    [Fact]
    public void Registry_MergesBundledAndCustomer_CustomerWins()
    {
        var bundled = new[] { FormA() with { Name = "Bundled A" } };
        using var store = new TemplateStore(":memory:");
        store.Save(FormA() with { Name = "Customer A" });   // same id "form-a"

        var all = new TemplateRegistry(store, bundled).All();

        var a = Assert.Single(all);
        Assert.Equal("form-a", a.TemplateId);
        Assert.Equal("Customer A", a.Name);   // customer overrides bundled on id collision
    }

    [Fact]
    public void Registry_BundledOnly_WhenNoCustomerStore()
    {
        var registry = new TemplateRegistry(customerStore: null, bundled: new[] { FormA() });
        Assert.Single(registry.All());
    }

    [Fact]
    public void Router_RoutesMatchingPage_ToDeterministicFields()
    {
        var router = new TemplateRouter(new TemplateRegistry(null, new[] { FormA() }));

        var route = router.RoutePage(FilledFormPdf.Build(), 1);

        Assert.True(route.Matched);
        Assert.Equal("form-a", route.Match!.Template.TemplateId);
        Assert.Equal("ABC123-25-R-00001", Assert.Single(route.Fields).Value);
    }

    [Fact]
    public void Router_FallsBack_OnUnrecognizedPage()
    {
        // Registry knows only Form A (widget centre 0.5,0.2). An upload whose widget is elsewhere must NOT
        // match — a false match would apply wrong coordinates → wrong values. Miss + fall back is the safe path.
        var router = new TemplateRouter(new TemplateRegistry(null, new[] { FormA() }));

        byte[] different = FilledFormPdf.Build(rectLeft: 5, rectBottom: 10, rectRight: 40, rectTop: 30);
        var route = router.RoutePage(different, 1);

        Assert.False(route.Matched);
        Assert.Empty(route.Fields);
    }

    [Fact]
    public void Router_RouteDocument_RoutesEachPageIndependently()
    {
        var router = new TemplateRouter(new TemplateRegistry(null, new[] { FormA() }));

        var routes = router.RouteDocument(FilledFormPdf.Build());

        var r = Assert.Single(routes);
        Assert.Equal(1, r.Page);
        Assert.True(r.Matched);
    }
}
