using Tesseract;
using Xunit;

namespace SuavoAgent.Helper.Tests.Vision;

public sealed class TesseractManagedWrapperPackagingTests
{
    [Fact]
    public void Managed_wrapper_is_present_at_runtime()
    {
        var assembly = typeof(TesseractEngine).Assembly;

        Assert.Equal("Tesseract", assembly.GetName().Name);
        Assert.True(File.Exists(assembly.Location));
    }

    [Fact]
    public void Unapproved_package_native_binaries_are_absent_from_test_closure()
    {
        var forbidden = Directory
            .EnumerateFiles(AppContext.BaseDirectory, "*.dll", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Equals(
                               "tesseract50.dll",
                               StringComparison.OrdinalIgnoreCase) ||
                           Path.GetFileName(path).StartsWith(
                               "leptonica-",
                               StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(forbidden);
    }
}
