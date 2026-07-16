using System;
using System.Collections.Generic;
using System.Linq;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Agentic.Adapters;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>Pure ScreenFrame → PerceivedScreen mapping: grounding summary + deterministic content hash.</summary>
public sealed class PerceivedScreenMapperTests
{
    private static ScreenFrame Frame(
        VisualElement[]? elements = null,
        TextRegion[]? text = null,
        string? processName = null) => new()
    {
        Id = "f1",
        CapturedAt = DateTimeOffset.UnixEpoch,
        Width = 800,
        Height = 600,
        ExtractorId = "tesseract-5",
        ProcessName = processName,
        Elements = elements ?? Array.Empty<VisualElement>(),
        TextRegions = text ?? Array.Empty<TextRegion>(),
    };

    private static VisualElement El(
        string role,
        string? name,
        string? automationId = null,
        string? className = null,
        byte? structuralStateByte = null) =>
        new()
        {
            Role = role,
            Name = name,
            AutomationId = automationId,
            ClassName = className,
            StructuralStateByte = structuralStateByte,
            Bounds = new Rect(0, 0, 10, 10),
            Confidence = 1.0,
        };

    private static TextRegion Txt(string s) =>
        new() { Text = s, Bounds = new Rect(0, 0, 10, 10), Confidence = 90 };

    [Fact]
    public void FromFrame_BuildsElementSummary_RoleColonName()
    {
        var p = PerceivedScreenMapper.FromFrame(Frame(elements: new[] { El("button", "Save"), El("input", "NDC") }));

        Assert.Contains("button:Save", p.ElementSummary);
        Assert.Contains("input:NDC", p.ElementSummary);
    }

    [Fact]
    public void FromFrame_ElementWithoutName_UsesRoleOnly()
    {
        var p = PerceivedScreenMapper.FromFrame(Frame(elements: new[] { El("button", null) }));
        Assert.Contains("button", p.ElementSummary);
        Assert.DoesNotContain("button:", string.Join("|", p.ElementSummary));
    }

    [Fact]
    public void FromFrame_IncludesNonEmptyTextRegions()
    {
        var p = PerceivedScreenMapper.FromFrame(Frame(text: new[] { Txt("Rx Lookup"), Txt("   ") }));
        Assert.Contains("text:Rx Lookup", p.ElementSummary);
        Assert.DoesNotContain("text:   ", p.ElementSummary);
    }

    [Fact]
    public void FromFrame_IsAlwaysScrubbed()
    {
        Assert.True(PerceivedScreenMapper.FromFrame(Frame()).Scrubbed);
    }

    [Fact]
    public void FromFrame_SurfacesSignatures_ForElementsWithAutomationId()
    {
        var p = PerceivedScreenMapper.FromFrame(Frame(elements: new[]
        {
            El("Button", "Save", automationId: "saveBtn"),
            El("Text", "label", automationId: null), // no AutomationId ⇒ no signature
        }));

        Assert.Contains("Button|saveBtn", p.Signatures!);
        Assert.Single(p.Signatures!); // the AutomationId-less element contributes none
    }

    [Fact]
    public void FromFrame_CarriesOnlyStructuralProcessAndTypedFingerprints()
    {
        var p = PerceivedScreenMapper.FromFrame(Frame(
            elements:
            [
                El("Button", "Patient Jane", "btnSave", "WpfButton", 0xA5),
                El("Text", "Dynamic label"),
            ],
            processName: "PioneerRx.exe"));

        Assert.Equal("PioneerRx.exe", p.ProcessName);
        var fingerprint = Assert.Single(p.ElementFingerprints!);
        Assert.Equal("Button", fingerprint.ControlType);
        Assert.Equal("btnSave", fingerprint.AutomationId);
        Assert.Equal("WpfButton", fingerprint.ClassName);
        var state = Assert.Single(p.StructuralElementStates!);
        Assert.Equal(fingerprint, state.Signature);
        Assert.Equal(0xA5, state.StateByte);
        Assert.True(p.CloudStructuralStateEligible);
    }

    [Fact]
    public void FromFrame_UnsupportedStructuralProperty_MakesCloudStateIneligible()
    {
        var p = PerceivedScreenMapper.FromFrame(Frame(
            elements:
            [
                El("Button", "Save", "btnSave", "WpfButton", 0xFF),
                El("Edit", "NDC", "txtNdc", "WpfEdit", null),
            ],
            processName: "PioneerRx.exe"));

        Assert.False(p.CloudStructuralStateEligible);
    }

    [Fact]
    public void FromFrame_CloudStructuralState_IsStableDeduplicatedAndCappedAtEight()
    {
        var elements = Enumerable.Range(0, 10)
            .Reverse()
            .Select(index => El(
                "Button",
                "dynamic",
                $"btn{index:D2}",
                "WpfButton",
                (byte)index))
            .Append(El("Button", "duplicate", "btn00", "WpfButton", 0x00))
            .ToArray();

        var p = PerceivedScreenMapper.FromFrame(Frame(
            elements: elements,
            processName: "PioneerRx.exe"));

        Assert.True(p.CloudStructuralStateEligible);
        Assert.Equal(8, p.StructuralElementStates!.Count);
        Assert.Equal(
            Enumerable.Range(0, 8).Select(index => $"btn{index:D2}"),
            p.StructuralElementStates.Select(value => value.Signature.AutomationId));
    }

    [Fact]
    public void FromFrame_ConflictingDuplicateIdentity_MakesCloudStateIneligible()
    {
        var p = PerceivedScreenMapper.FromFrame(Frame(
            elements:
            [
                El("Button", "one", "btnSave", "WpfButton", 0xFF),
                El("Button", "two", "btnSave", "WpfButton", 0xFE),
            ],
            processName: "PioneerRx.exe"));

        Assert.False(p.CloudStructuralStateEligible);
    }

    [Fact]
    public void FromFrame_SameContent_SameHash_RegardlessOfTimestampOrExtractor()
    {
        var a = new ScreenFrame { Id = "a", CapturedAt = DateTimeOffset.UnixEpoch, Width = 1, Height = 1, ExtractorId = "x", Elements = new[] { El("button", "Save") } };
        var b = new ScreenFrame { Id = "b", CapturedAt = DateTimeOffset.UtcNow, Width = 9, Height = 9, ExtractorId = "y", Elements = new[] { El("button", "Save") } };

        Assert.Equal(PerceivedScreenMapper.FromFrame(a).ContentHash, PerceivedScreenMapper.FromFrame(b).ContentHash);
    }

    [Fact]
    public void FromFrame_DifferentContent_DifferentHash()
    {
        var a = PerceivedScreenMapper.FromFrame(Frame(elements: new[] { El("button", "Save") }));
        var b = PerceivedScreenMapper.FromFrame(Frame(elements: new[] { El("button", "Cancel") }));

        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }
}
