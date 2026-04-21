using SuavoAgent.Contracts.Discovery;

namespace SuavoAgent.Core.Verticals.Pharmacy;

/// <summary>
/// The built-in pharmacy pack — in v3.13 this lives as a static instance;
/// in v3.14 the same <see cref="VerticalPack"/> will load from a signed
/// JSON at <c>%ProgramData%\SuavoAgent\packs\pharmacy_rx.v1.pack.json</c>
/// and this class will go away. Call sites use
/// <see cref="PharmacyPresets"/> (not this class directly) so the swap is
/// a loader change, nothing else.
/// </summary>
public static class PharmacyRxPack
{
    /// <summary>
    /// NDC pattern covering PioneerRx display formats and common billing
    /// variants:
    /// <list type="bullet">
    ///   <item>Labeler(5)-Product(4)-Package(2), hyphenated: <c>55111-0645-01</c></item>
    ///   <item>Labeler(4)-Product(4)-Package(2), hyphenated: <c>0781-5180-01</c></item>
    ///   <item>Labeler(5)-Product(3)-Package(2), hyphenated: <c>59762-0323-01</c></item>
    ///   <item>Unhyphenated 11-digit billing format: <c>55111064501</c></item>
    ///   <item>Unhyphenated 10-digit legacy format: <c>5511106450</c></item>
    /// </list>
    /// </summary>
    public const string NdcRegex =
        @"^(?:" +
        @"\d{5}-\d{4}-\d{2}" +
        @"|\d{4}-\d{4}-\d{2}" +
        @"|\d{5}-\d{3}-\d{2}" +
        @"|\d{11}" +
        @"|\d{10}" +
        @")$";

    public static readonly VerticalPack Instance = new(
        Id: "pharmacy_rx",
        Version: "1.0.0",
        DisplayName: "Pharmacy (Rx)",
        NameHintPriors: new[]
        {
            "generic", "ndc", "formulary", "pricing", "dispensed", "top", "rxlist",
        },
        CommonPrimaryKeyPatterns: new[]
        {
            new ExpectedColumnPattern(
                Name: "NDC",
                Regex: NdcRegex,
                MinSampleMatches: 3),
        },
        // Note: .xls (legacy OLE2 binary) deliberately omitted — EPPlus
        // only reads the modern .xlsx (OOXML) format, so advertising .xls
        // would surface candidates the sampler can't actually open.
        CommonExtensions: new[] { ".xlsx", ".csv" });
}
