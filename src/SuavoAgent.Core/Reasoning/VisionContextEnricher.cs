using System.Collections.Generic;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// The single seam where a captured <see cref="ScreenFrame"/> meets reasoning: merges the
/// frame's PHI-scrubbed OCR text + visual elements into a <see cref="RuleContext"/>, and unions
/// element names into <see cref="RuleContext.VisibleElements"/> so existing name-based predicates
/// also benefit. Pure — keeps <see cref="RuleContext"/> a dumb record and the worker wiring thin.
/// </summary>
public static class VisionContextEnricher
{
    public static RuleContext Enrich(RuleContext ctx, ScreenFrame frame)
    {
        var visible = new HashSet<string>(ctx.VisibleElements);
        foreach (var element in frame.Elements)
        {
            if (!string.IsNullOrEmpty(element.Name))
                visible.Add(element.Name);
        }

        return ctx with
        {
            ScreenText = frame.TextRegions,
            ScreenElements = frame.Elements,
            VisibleElements = visible,
        };
    }
}
