using ZeroDep.Abstractions;

namespace Foliant.Orchestration;

/// <summary>
/// Maps ZeroDep's <see cref="PageContentClass"/> to the orchestrator's engine-agnostic
/// <see cref="PageKind"/>. This is the one intentional ZeroDep binding in Phase 0 — a documented, stable
/// enum — and it anchors the ZeroDep package reference for the dependency-direction gate.
/// A document-level reject/encrypt result is handled upstream and maps to
/// <see cref="PageKind.Unprocessable"/>; it never appears as a <see cref="PageContentClass"/>.
/// </summary>
/// <remarks>
/// BUILD NOTE — these enum member names are taken from the ZeroDep 1.6.0 CHANGELOG. If the published
/// <c>ZeroDep.Abstractions.PageContentClass</c> differs, fix the mapping here (one place).
/// </remarks>
internal static class PageKindMapping
{
    public static PageKind FromZeroDep(PageContentClass cls) => cls switch
    {
        PageContentClass.Empty                => PageKind.Empty,
        PageContentClass.DigitalText          => PageKind.DigitalText,
        PageContentClass.FormPage             => PageKind.FormPage,
        PageContentClass.TableOrComplexLayout => PageKind.TableOrComplexLayout,
        PageContentClass.ScannedImageOnly     => PageKind.ScannedImageOnly,
        PageContentClass.ScannedWithOcr       => PageKind.ScannedWithOcr,
        PageContentClass.Mixed                => PageKind.Mixed,
        _                                     => PageKind.Mixed, // unknown/future class → escalate (Mixed is heavy)
    };
}
