using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SuavoAgent.Analyzers;

/// <summary>
/// SUAVO0001: emits an error diagnostic when a property/field marked
/// <c>[PhiDirect]</c> appears (directly or transitively through nested type
/// references) on a type marked <c>[OutboundPayload]</c>.
///
/// Walks the type graph with a HashSet&lt;INamedTypeSymbol&gt; visited set
/// (cycle guard) and a depth cap of 50 levels. Only follows types defined
/// in the same assembly as the outbound type — framework + external types
/// are out of scope.
///
/// Crash-loud: any unhandled exception inside the analyzer surfaces as
/// SUAVO0099 rather than failing silently. Roslyn's default behavior for
/// crashed analyzers is silent suppression — unacceptable for a HIPAA gate.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PhiInOutboundPayloadAnalyzer : DiagnosticAnalyzer
{
    private const string OutboundAttributeFullName =
        "SuavoAgent.Contracts.Annotations.OutboundPayloadAttribute";
    private const string PhiAttributeFullName =
        "SuavoAgent.Contracts.Annotations.PhiDirectAttribute";
    private const int MaxDepth = 50;

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            DiagnosticDescriptors.PhiInOutboundPayload,
            DiagnosticDescriptors.AnalyzerInternalError);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext ctx)
    {
        var symbol = (INamedTypeSymbol)ctx.Symbol;
        try
        {
            if (!HasAttribute(symbol, OutboundAttributeFullName))
                return;

            var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var path = new Stack<string>();
            WalkMembers(symbol, symbol, visited, path, ctx, depth: 0);
        }
        catch (Exception ex)
        {
            ReportInternalError(ctx, symbol, ex);
        }
    }

    private static void WalkMembers(
        INamedTypeSymbol outboundRoot,
        INamedTypeSymbol current,
        HashSet<INamedTypeSymbol> visited,
        Stack<string> path,
        SymbolAnalysisContext ctx,
        int depth)
    {
        if (depth > MaxDepth)
        {
            var location = current.Locations.FirstOrDefault() ?? Location.None;
            ctx.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.AnalyzerInternalError,
                location,
                outboundRoot.ToDisplayString(),
                $"max nesting depth ({MaxDepth}) exceeded at {current.Name}"));
            return;
        }

        if (!visited.Add(current))
            return;

        foreach (var member in current.GetMembers())
        {
            if (member is not (IPropertySymbol or IFieldSymbol))
                continue;

            // Skip compiler-generated backing fields, etc. Their names are
            // <PropName>k__BackingField which would corrupt diagnostic paths.
            if (member.IsImplicitlyDeclared)
                continue;

            // Direct check: does the member itself carry [PhiDirect]?
            if (HasAttribute(member, PhiAttributeFullName))
            {
                var location = member.Locations.FirstOrDefault() ?? Location.None;
                var fieldPath = path.Count == 0
                    ? $"{outboundRoot.Name}.{member.Name}"
                    : $"{outboundRoot.Name}.{string.Join(".", path.Reverse())}.{member.Name}";

                ctx.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.PhiInOutboundPayload,
                    location,
                    fieldPath,
                    outboundRoot.ToDisplayString()));
            }

            // Recurse into the member's type if it's defined in the same assembly.
            var memberType = GetMemberType(member);
            if (memberType is INamedTypeSymbol nested
                && nested.ContainingAssembly is not null
                && SymbolEqualityComparer.Default.Equals(
                    nested.ContainingAssembly,
                    outboundRoot.ContainingAssembly))
            {
                path.Push(member.Name);
                WalkMembers(outboundRoot, nested, visited, path, ctx, depth + 1);
                path.Pop();
            }
        }
    }

    private static ITypeSymbol? GetMemberType(ISymbol member) => member switch
    {
        IPropertySymbol p => p.Type,
        IFieldSymbol f => f.Type,
        _ => null,
    };

    private static bool HasAttribute(ISymbol symbol, string fullName) =>
        symbol.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == fullName);

    private static void ReportInternalError(
        SymbolAnalysisContext ctx,
        INamedTypeSymbol symbol,
        Exception ex)
    {
        var location = symbol.Locations.FirstOrDefault() ?? Location.None;
        ctx.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.AnalyzerInternalError,
            location,
            symbol.ToDisplayString(),
            $"{ex.GetType().Name}: {ex.Message}"));
    }
}
