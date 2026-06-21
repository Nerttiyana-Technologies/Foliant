using System.Text.RegularExpressions;

namespace Foliant.Pipeline;

/// <summary>
/// Identifies a U.S. federal Standard/Optional Form from a page's recognized text by its printed
/// designation — every such form prints "STANDARD FORM 1449" / "OPTIONAL FORM 347" (the form stating its
/// own identity, present on every page). Returns the bare number ("1449", "25A", "347") or null.
///
/// Abstains by design: an unrecognized page returns null and gets no form-specific handling, so federal-
/// only behaviour (e.g. <see cref="FederalFormTableRenderer"/>) can never affect non-form documents.
/// </summary>
public static class FormIdentifier
{
    // Anchored on the form's OWN designation ("STANDARD FORM 1449", "STANDARD FORM 25-B"), not a bare
    // "SF 30" that could appear in body text — keeps false positives near zero. Number 1–4 digits with an
    // optional letter suffix (25A), optional hyphen/space before the suffix.
    private static readonly Regex Designation = new(
        @"\b(?:STANDARD|OPTIONAL)\s+FORM\s+([0-9]{1,4})\s*-?\s*([A-Z])?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>The form number (e.g. "1449", "25A"), or null when no confident designation is found.</summary>
    public static string? Identify(IReadOnlyList<TextLine> lines)
    {
        if (lines is null) return null;
        foreach (var line in lines)
        {
            var m = Designation.Match(line.Text ?? string.Empty);
            if (!m.Success) continue;
            string suffix = m.Groups[2].Success ? m.Groups[2].Value.ToUpperInvariant() : string.Empty;
            return m.Groups[1].Value + suffix;
        }
        return null;
    }

    /// <summary>True when the page is a recognized federal Standard/Optional Form.</summary>
    public static bool IsFederalForm(IReadOnlyList<TextLine> lines) => Identify(lines) is not null;
}
