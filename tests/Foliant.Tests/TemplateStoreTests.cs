using Foliant;
using Foliant.Templates;
using Xunit;

namespace Foliant.Tests;

public sealed class TemplateStoreTests
{
    private static FormLayout SampleLayout() => new(
        TemplateId: "sf1449-rev2021",
        Name: "SF-1449 Rev 11/2021",
        Elements: new[]
        {
            new FormElement(FormElementKind.Checkbox, 1, new NormalizedRect(0.50f, 0.60f, 0.52f, 0.62f),
                "27b ADDENDA — ARE NOT ATTACHED", "27b"),
            new FormElement(FormElementKind.Text, 1, new NormalizedRect(0.10f, 0.10f, 0.30f, 0.12f),
                "Solicitation Number"),
        },
        Fingerprint: "fp-abc");

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"foliant_tmpl_{Guid.NewGuid():N}.db");

    [Fact]
    public void Save_Get_RoundTripsGeometryAndLabels()
    {
        string db = TempDb();
        try
        {
            using var store = new TemplateStore(db);
            store.Save(SampleLayout());

            var got = store.Get("sf1449-rev2021");
            Assert.NotNull(got);
            Assert.Equal("SF-1449 Rev 11/2021", got!.Name);
            Assert.Equal("fp-abc", got.Fingerprint);
            Assert.Equal(2, got.Elements.Count);

            var cb = got.Elements[0];
            Assert.Equal(FormElementKind.Checkbox, cb.Kind);
            Assert.Equal("27b ADDENDA — ARE NOT ATTACHED", cb.Label);
            Assert.Equal("27b", cb.Group);
            Assert.Equal(1, cb.Page);
            Assert.Equal(0.50f, cb.Rect.X1, 3);
            Assert.Equal(0.62f, cb.Rect.Y2, 3);
        }
        finally { if (File.Exists(db)) File.Delete(db); }
    }

    [Fact]
    public void Save_Replaces_AndAllAndDeleteWork()
    {
        string db = TempDb();
        try
        {
            using var store = new TemplateStore(db);
            store.Save(SampleLayout());
            store.Save(SampleLayout() with { Name = "SF-1449 (renamed)" });   // replace, not duplicate

            Assert.Single(store.All());
            Assert.Equal("SF-1449 (renamed)", store.Get("sf1449-rev2021")!.Name);

            Assert.True(store.Delete("sf1449-rev2021"));
            Assert.Null(store.Get("sf1449-rev2021"));
            Assert.Empty(store.All());
        }
        finally { if (File.Exists(db)) File.Delete(db); }
    }
}
