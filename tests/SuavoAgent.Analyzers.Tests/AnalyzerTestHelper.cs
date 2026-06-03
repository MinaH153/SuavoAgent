using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SuavoAgent.Analyzers.Tests;

/// <summary>
/// Direct analyzer invocation helpers. Avoids the
/// Microsoft.CodeAnalysis.CSharp.Analyzer.Testing.XUnit wrapper which has known
/// xunit constructor compatibility issues. We compile a source string in-memory
/// and run the analyzer over it; tests then assert on the resulting
/// ImmutableArray&lt;Diagnostic&gt;.
/// </summary>
internal static class AnalyzerTestHelper
{
    /// <summary>
    /// Standard system references required for any analyzer test compilation.
    /// </summary>
    private static readonly ImmutableArray<MetadataReference> StandardReferences =
        BuildStandardReferences();

    private static ImmutableArray<MetadataReference> BuildStandardReferences()
    {
        var trustedAssembliesPaths =
            ((string?)System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator)
                ?? new[]
                {
                    typeof(object).Assembly.Location,
                    typeof(System.Attribute).Assembly.Location,
                    typeof(System.Collections.Generic.IEnumerable<>).Assembly.Location,
                    typeof(System.Linq.Enumerable).Assembly.Location,
                };

        return trustedAssembliesPaths
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }

    /// <summary>
    /// Compile <paramref name="source"/>, run <typeparamref name="TAnalyzer"/>
    /// over the resulting compilation, and return the analyzer diagnostics.
    /// Compilation diagnostics (CS errors/warnings) are excluded — the test
    /// asserts only on analyzer-emitted diagnostics.
    /// </summary>
    public static async Task<ImmutableArray<Diagnostic>> RunAnalyzerAsync<TAnalyzer>(string source)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            assemblyName: "SuavoAgent.AnalyzerUnderTest",
            syntaxTrees: new[] { tree },
            references: StandardReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new TAnalyzer());
        var withAnalyzers = compilation.WithAnalyzers(analyzers);
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    /// <summary>
    /// Convenience: assert that the analyzer emitted exactly one diagnostic
    /// matching the given rule id; return that diagnostic for further
    /// inspection.
    /// </summary>
    public static Diagnostic SingleDiagnostic(
        ImmutableArray<Diagnostic> diagnostics,
        string ruleId)
    {
        var matching = diagnostics.Where(d => d.Id == ruleId).ToArray();
        if (matching.Length != 1)
        {
            throw new System.InvalidOperationException(
                $"Expected exactly 1 diagnostic with id {ruleId}, got {matching.Length}. " +
                $"All diagnostics: [{string.Join(", ", diagnostics.Select(d => d.Id))}]");
        }
        return matching[0];
    }
}
