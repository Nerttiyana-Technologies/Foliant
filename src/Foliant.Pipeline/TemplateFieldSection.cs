using System.Text;

namespace Foliant.Pipeline;

/// <summary>
/// Renders a recognized template's deterministic fields as a Markdown section. APPENDED to a matched page's
/// Markdown — additive, so it cannot reduce recall or drop existing content, while giving downstream consumers
/// (e.g. a Q&amp;A tool that ingests the Markdown) the KNOWN-correct, label-bound values: a checkbox like
/// "27b ADDENDA — ARE NOT ATTACHED" is unambiguous instead of a bare "[X]" the reader must localize.
/// </summary>
internal static class TemplateFieldSection
{
    public static string Render(PageTemplateMatch match)
    {
        var checkboxes = match.Fields.Where(f => f.Kind == FieldKind.Checkbox).ToList();
        var texts = match.Fields.Where(f => f.Kind == FieldKind.Text).ToList();

        var sb = new StringBuilder();
        sb.Append("<!-- Foliant: template '").Append(match.TemplateId)
          .Append("' (match ").Append(match.Score.ToString("F2")).Append(") -->\n\n");
        sb.Append("### Form fields — ").Append(match.TemplateId).Append("\n\n");

        if (checkboxes.Count > 0)
        {
            sb.Append("**Selected:**\n\n");
            foreach (var c in checkboxes)
                sb.Append("- [x] ").Append(c.Value).Append('\n');   // Value is the selected option's label
            sb.Append('\n');
        }

        if (texts.Count > 0)
        {
            foreach (var t in texts)
                sb.Append("- **").Append(t.Name).Append(":** ").Append(t.Value).Append('\n');
        }

        return sb.ToString();
    }
}
