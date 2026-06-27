using ZD = ZeroDep.Abstractions;

namespace Foliant.Orchestration;

/// <summary>
/// Maps ZeroDep's structural output to Foliant's core types so everything downstream (Markdown composer,
/// FoliantView) is unchanged regardless of which lane produced a page. With the classification reader, this
/// is one of the only places that names ZeroDep types (ADR-0003 Phase-1 type-reconciliation seam).
/// </summary>
public interface ITypeAdapter
{
    /// <summary>ZeroDep <see cref="ZD.TextRunInfo"/> → Foliant <see cref="TextLine"/>.</summary>
    TextLine ToTextLine(ZD.TextRunInfo run);

    /// <summary>
    /// ZeroDep <see cref="ZD.FormFieldInfo"/> → Foliant <see cref="FormField"/>, or <c>null</c> when the
    /// field has no usable value (e.g. an empty signature field) and should be skipped.
    /// </summary>
    FormField? ToFormField(ZD.FormFieldInfo field);
}
