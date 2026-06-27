using System.Reflection;
using Xunit;

namespace Foliant.Orchestration.Tests;

/// <summary>
/// Gate G0 — the cardinal one-way-dependency rule made executable (ADR-0003): Foliant may reference ZeroDep;
/// ZeroDep must <b>never</b> reference Foliant. We assert it by loading the ZeroDep assemblies and checking
/// that none of their referenced assemblies belong to Foliant. This runs on every build.
/// </summary>
public sealed class DependencyDirectionTests
{
    private static readonly string[] ZeroDepAssemblies = { "ZeroDep", "ZeroDep.Abstractions" };

    [Fact]
    public void ZeroDep_assemblies_do_not_reference_Foliant()
    {
        var checkedAny = false;

        foreach (var name in ZeroDepAssemblies)
        {
            Assembly asm;
            try
            {
                asm = Assembly.Load(new AssemblyName(name));
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                // If the package layout names the assembly differently, skip rather than false-fail; at least
                // one ZeroDep assembly must load (asserted below).
                continue;
            }

            checkedAny = true;

            var foliantRefs = asm.GetReferencedAssemblies()
                .Where(r => r.Name is not null &&
                            r.Name.StartsWith("Foliant", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Name)
                .ToList();

            Assert.True(
                foliantRefs.Count == 0,
                $"ZeroDep assembly '{name}' references Foliant — the one-way dependency rule is violated: " +
                string.Join(", ", foliantRefs));
        }

        Assert.True(checkedAny,
            "No ZeroDep assembly could be loaded. Confirm the ZeroDep NuGet package reference resolves " +
            "(expected assembly names: " + string.Join(", ", ZeroDepAssemblies) + ").");
    }

    [Fact]
    public void Orchestrator_references_ZeroDep_as_a_package()
    {
        // The orchestrator must depend on ZeroDep (so the fast lane has something to call). This confirms the
        // reference exists; the csproj guarantees it is a NuGet PackageReference, not a local project ref.
        var orchestrator = typeof(DocumentOrchestrator).Assembly;

        var referencesZeroDep = orchestrator.GetReferencedAssemblies()
            .Any(r => r.Name is not null &&
                      r.Name.StartsWith("ZeroDep", StringComparison.OrdinalIgnoreCase));

        Assert.True(referencesZeroDep,
            "Foliant.Orchestration does not reference any ZeroDep assembly — expected a ZeroDep package reference.");
    }
}
