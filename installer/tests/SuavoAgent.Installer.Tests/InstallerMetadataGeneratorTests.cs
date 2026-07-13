using System.Security.Cryptography;
using System.Text.Json;
using SuavoAgent.Installer.Metadata;

namespace SuavoAgent.Installer.Tests;

public sealed class InstallerMetadataGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-installer-metadata-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Generate_WritesExactCohortAndDeterministicBytes()
    {
        var payloads = CreatePayloads();
        var firstOutput = Path.Combine(_root, "first");
        var secondOutput = Path.Combine(_root, "second");
        var request = new InstallerMetadataRequest(
            "4.2.1",
            "2026-07-12T12:00:00.0000000+00:00",
            firstOutput,
            payloads);

        var first = InstallerMetadataGenerator.Generate(request);
        var second = InstallerMetadataGenerator.Generate(request with
        {
            OutputDirectory = secondOutput,
        });

        Assert.Equal(File.ReadAllBytes(first.ManifestPath), File.ReadAllBytes(second.ManifestPath));
        Assert.Equal(File.ReadAllBytes(first.InstallStatePath), File.ReadAllBytes(second.InstallStatePath));
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(first.ManifestPath));
        Assert.Equal(
            InstallerMetadataGenerator.InstalledCohort,
            manifest.RootElement.EnumerateObject().Select(static property => property.Name));
        foreach (var name in InstallerMetadataGenerator.InstalledCohort)
        {
            var expected = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(payloads[name]))).ToLowerInvariant();
            Assert.Equal(expected, manifest.RootElement.GetProperty(name).GetString());
        }

        using var state = JsonDocument.Parse(File.ReadAllBytes(first.InstallStatePath));
        Assert.Equal(1, state.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("native-maintenance-bridge", state.RootElement.GetProperty("installerKind").GetString());
        Assert.Equal("4.2.1", state.RootElement.GetProperty("version").GetString());
        Assert.Equal(
            "2026-07-12T12:00:00.0000000+00:00",
            state.RootElement.GetProperty("installedAtUtc").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("4")]
    [InlineData("4.2")]
    [InlineData("4.2.1.9")]
    [InlineData("v4.2.1")]
    [InlineData("256.2.1")]
    [InlineData("4.256.1")]
    [InlineData("4.2.65536")]
    public void Generate_RejectsNonMsiVersion(string version)
    {
        var request = ValidRequest() with { Version = version };
        Assert.Throws<ArgumentException>(() => InstallerMetadataGenerator.Generate(request));
    }

    [Fact]
    public void Generate_RejectsMissingCohortMember()
    {
        var payloads = CreatePayloads()
            .Where(static pair => pair.Key != "SuavoAgent.Helper.exe")
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        var request = ValidRequest() with { BinaryPaths = payloads };
        Assert.Throws<ArgumentException>(() => InstallerMetadataGenerator.Generate(request));
    }

    [Fact]
    public void Generate_RejectsMissingPayloadFile()
    {
        var payloads = CreatePayloads().ToDictionary(
            static pair => pair.Key,
            pair => pair.Key == "SuavoAgent.Helper.exe"
                ? Path.Combine(_root, "missing.exe")
                : pair.Value,
            StringComparer.Ordinal);
        var request = ValidRequest() with { BinaryPaths = payloads };
        Assert.Throws<FileNotFoundException>(() => InstallerMetadataGenerator.Generate(request));
    }

    [Theory]
    [InlineData("2026-07-12")]
    [InlineData("2026-07-12T12:00:00Z")]
    [InlineData("2026-07-12T12:00:00.0000000+01:00")]
    [InlineData("not-a-timestamp")]
    public void Generate_RejectsNonRoundTripTimestamp(string timestamp)
    {
        var request = ValidRequest() with { InstalledAtUtc = timestamp };
        Assert.Throws<ArgumentException>(() => InstallerMetadataGenerator.Generate(request));
    }

    [Fact]
    public void CommandLine_ParsesExactReleaseInputs()
    {
        var payloads = CreatePayloads();
        var arguments = new List<string>
        {
            "--version", "4.2.1",
            "--timestamp", "2026-07-12T12:00:00.0000000+00:00",
            "--output-dir", Path.Combine(_root, "output"),
        };
        foreach (var (name, path) in payloads)
        {
            arguments.Add("--binary");
            arguments.Add($"{name}={path}");
        }

        var request = CommandLine.Parse(arguments);

        Assert.Equal("4.2.1", request.Version);
        Assert.Equal("2026-07-12T12:00:00.0000000+00:00", request.InstalledAtUtc);
        Assert.Equal(Path.Combine(_root, "output"), request.OutputDirectory);
        Assert.Equal(payloads, request.BinaryPaths);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--timestamp")]
    [InlineData("--output-dir")]
    public void CommandLine_RejectsMissingOrDuplicateSingleValue(string option)
    {
        var valid = new List<string>
        {
            "--version", "4.2.1",
            "--timestamp", "2026-07-12T12:00:00.0000000+00:00",
            "--output-dir", Path.Combine(_root, "output"),
        };
        valid.RemoveAt(valid.IndexOf(option) + 1);

        Assert.Throws<ArgumentException>(() => CommandLine.Parse(valid));

        valid.Add(option);
        valid.Add("duplicate");
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(valid));
    }

    [Theory]
    [InlineData("missing-separator")]
    [InlineData("=missing-name")]
    [InlineData("missing-path=")]
    public void CommandLine_RejectsMalformedBinary(string specification)
    {
        var arguments = new[]
        {
            "--version", "4.2.1",
            "--timestamp", "2026-07-12T12:00:00.0000000+00:00",
            "--output-dir", Path.Combine(_root, "output"),
            "--binary", specification,
        };

        Assert.Throws<ArgumentException>(() => CommandLine.Parse(arguments));
    }

    [Fact]
    public void CommandLine_RejectsDuplicateBinaryNames()
    {
        var arguments = new[]
        {
            "--version", "4.2.1",
            "--timestamp", "2026-07-12T12:00:00.0000000+00:00",
            "--output-dir", Path.Combine(_root, "output"),
            "--binary", "SuavoAgent.Core.exe=first",
            "--binary", "SuavoAgent.Core.exe=second",
        };

        Assert.Throws<ArgumentException>(() => CommandLine.Parse(arguments));
    }

    [Theory]
    [InlineData("--unknown", "value")]
    [InlineData("--version")]
    public void CommandLine_RejectsUnknownOrUnpairedArguments(params string[] trailingArguments)
    {
        var arguments = new[]
        {
            "--version", "4.2.1",
            "--timestamp", "2026-07-12T12:00:00.0000000+00:00",
            "--output-dir", Path.Combine(_root, "output"),
        }.Concat(trailingArguments).ToArray();

        Assert.Throws<ArgumentException>(() => CommandLine.Parse(arguments));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private InstallerMetadataRequest ValidRequest() => new(
        "4.2.1",
        "2026-07-12T12:00:00.0000000+00:00",
        Path.Combine(_root, "output"),
        CreatePayloads());

    private IReadOnlyDictionary<string, string> CreatePayloads()
    {
        var payloadRoot = Path.Combine(_root, "payloads");
        Directory.CreateDirectory(payloadRoot);
        return InstallerMetadataGenerator.InstalledCohort.ToDictionary(
            static name => name,
            name =>
            {
                var path = Path.Combine(payloadRoot, name);
                File.WriteAllText(path, "payload:" + name);
                return path;
            },
            StringComparer.Ordinal);
    }
}
