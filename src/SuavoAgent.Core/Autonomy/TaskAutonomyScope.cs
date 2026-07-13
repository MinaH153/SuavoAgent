using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Autonomy;

public sealed record AutonomyEvidenceScope(
    string TaskType,
    string TaskKey,
    string AppId,
    string AppVersion,
    string SelectorDigest,
    string TemplateDigest,
    string ModelDigest,
    string ExecutorMode)
{
    [JsonIgnore]
    public string ScopeDigest => TaskAutonomyScope.Digest(this);
}

/// <summary>
/// Content-bound autonomy identity. Eligibility earned by one execution class
/// cannot be replayed for another app, verb, or executor merely by reusing a
/// caller-controlled taskKey.
/// </summary>
public static class TaskAutonomyScope
{
    private static readonly System.Text.RegularExpressions.Regex SafeToken = new(
        "^[a-z][a-z0-9_.:-]{0,99}$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex SafeVersion = new(
        "^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex DigestShape = new(
        "^[a-f0-9]{64}$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static AutonomyEvidenceScope Create(
        string taskType,
        string taskKey,
        string appId,
        string appVersion,
        string selectorDigest,
        string templateDigest,
        string modelDigest,
        PricingExecutorMode executorMode)
    {
        var scope = new AutonomyEvidenceScope(
            NormalizeToken(taskType),
            NormalizeToken(taskKey),
            NormalizeToken(appId),
            appVersion.Trim(),
            selectorDigest,
            templateDigest,
            modelDigest,
            NormalizeToken(executorMode.ToString()));
        if (!SafeVersion.IsMatch(scope.AppVersion) ||
            !DigestShape.IsMatch(scope.SelectorDigest) ||
            !DigestShape.IsMatch(scope.TemplateDigest) ||
            !DigestShape.IsMatch(scope.ModelDigest))
            throw new InvalidOperationException("Autonomy evidence scope is invalid.");
        return scope;
    }

    public static string Digest(AutonomyEvidenceScope scope)
    {
        var canonical = DeviceAuthorityCanonical.Serialize(scope);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    public static string ComponentDigest(string domain, params string[] values)
    {
        var canonical = string.Join("\n", new[] { domain }.Concat(values));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    public static string Build(
        string? taskKey,
        string? appIdentity,
        string? actionClass,
        PricingExecutorMode executorMode)
    {
        var canonical = string.Concat(
            Component(taskKey),
            Component(ProtectedDesktopProcessClassifier.CanonicalProcessStem(appIdentity)),
            Component(actionClass),
            Component(executorMode.ToString()));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        return $"scope:v1:{digest}";
    }

    private static string Component(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return $"{Encoding.UTF8.GetByteCount(normalized)}:{normalized}";
    }

    private static string NormalizeToken(string value)
    {
        var normalized = value.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        if (!SafeToken.IsMatch(normalized))
            throw new InvalidOperationException("Autonomy scope token is invalid.");
        return normalized;
    }
}
