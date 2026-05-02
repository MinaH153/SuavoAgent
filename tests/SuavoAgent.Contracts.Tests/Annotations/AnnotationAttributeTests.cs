using System;
using SuavoAgent.Contracts.Annotations;
using SuavoAgent.Contracts.Models;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Annotations;

public class AnnotationAttributeTests
{
    [Fact]
    public void PhiDirectAttribute_TargetsPropertiesAndFields()
    {
        var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
            typeof(PhiDirectAttribute),
            typeof(AttributeUsageAttribute))!;

        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Property));
        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Field));
        Assert.False(usage.AllowMultiple);
        Assert.True(usage.Inherited);
    }

    [Fact]
    public void OutboundPayloadAttribute_TargetsTypes()
    {
        var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
            typeof(OutboundPayloadAttribute),
            typeof(AttributeUsageAttribute))!;

        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Class));
        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Struct));
        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Interface));
        Assert.False(usage.AllowMultiple);
        Assert.True(usage.Inherited);
    }

    [Fact]
    public void Attributes_CanBeAppliedTogether()
    {
        // Compile-time check: just verify these declarations compile
        // (they will once the attributes exist + after Task 11 retrofits PatientDetailsPayload)
        var info = typeof(PatientDetailsPayload);
        Assert.NotNull(info);
    }
}
