using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Config;
using SuavoAgent.Helper.Vision;
using Tesseract;
using Xunit;

namespace SuavoAgent.Helper.Tests.Vision;

public sealed class TesseractNativeLoadBoundaryTests : IDisposable
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-tesseract-load-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TryPrepare_VerifiesManifestBeforeAnyNativeOrFallbackOperation()
    {
        var fixture = CreateFixture();
        var sequence = new List<string>();
        fixture.Platform.OnOperation = operation => sequence.Add(operation);
        var boundary = fixture.CreateBoundary(options =>
        {
            sequence.Add("verify-manifest");
            return false;
        });

        Assert.False(boundary.TryPrepare(fixture.Options, Log));

        Assert.Equal(["verify-manifest"], sequence);
        Assert.Empty(fixture.Platform.LoadCalls);
        Assert.Equal(0, fixture.Platform.WrapperSearchPathSetCount);
    }

    [Theory]
    [InlineData(WrapperFallbackRoot.Assembly, TesseractNativeLoadBoundary.LeptonicaFileName)]
    [InlineData(WrapperFallbackRoot.Assembly, TesseractNativeLoadBoundary.TesseractFileName)]
    [InlineData(WrapperFallbackRoot.Base, TesseractNativeLoadBoundary.LeptonicaFileName)]
    [InlineData(WrapperFallbackRoot.Base, TesseractNativeLoadBoundary.TesseractFileName)]
    [InlineData(WrapperFallbackRoot.BaseBin, TesseractNativeLoadBoundary.LeptonicaFileName)]
    [InlineData(WrapperFallbackRoot.BaseBin, TesseractNativeLoadBoundary.TesseractFileName)]
    [InlineData(WrapperFallbackRoot.CurrentDirectory, TesseractNativeLoadBoundary.LeptonicaFileName)]
    [InlineData(WrapperFallbackRoot.CurrentDirectory, TesseractNativeLoadBoundary.TesseractFileName)]
    public void TryPrepare_RejectsEveryPinnedWrapperFallbackBeforePreload(
        WrapperFallbackRoot fallback,
        string libraryFileName)
    {
        var fixture = CreateFixture();
        var fallbackX64 = fallback switch
        {
            WrapperFallbackRoot.Assembly => Path.Combine(
                fixture.Platform.TesseractAssemblyDirectory, "x64"),
            WrapperFallbackRoot.Base => Path.Combine(fixture.Platform.BaseDirectory, "x64"),
            WrapperFallbackRoot.BaseBin => Path.Combine(
                fixture.Platform.BaseDirectory, "bin", "x64"),
            WrapperFallbackRoot.CurrentDirectory => Path.Combine(
                fixture.Platform.CurrentDirectory, "x64"),
            _ => throw new ArgumentOutOfRangeException(nameof(fallback)),
        };
        Directory.CreateDirectory(fallbackX64);
        File.WriteAllText(
            Path.Combine(fallbackX64, libraryFileName),
            "unapproved");

        Assert.False(fixture.CreateBoundary().TryPrepare(fixture.Options, Log));

        Assert.Empty(fixture.Platform.LoadCalls);
        Assert.Equal(0, fixture.Platform.WrapperSearchPathSetCount);
    }

    [Fact]
    public void ExactDependencySearchFlags_AllowOnlyDllDirectoryAndSystem32()
    {
        const uint loadLibrarySearchDllLoadDir = 0x00000100;
        const uint loadLibrarySearchSystem32 = 0x00000800;

        Assert.Equal(
            loadLibrarySearchDllLoadDir | loadLibrarySearchSystem32,
            TesseractNativeLoadBoundary.ExactDependencySearchFlags);
    }

    [Fact]
    public void TryPrepare_RejectsFallbackDirectoryAliasToApprovedCohort()
    {
        var fixture = CreateFixture();
        var fallbackX64 = Path.Combine(fixture.Platform.BaseDirectory, "x64");
        Directory.CreateDirectory(fallbackX64);
        foreach (var library in ExpectedLibraryPaths(fixture.Options))
        {
            File.WriteAllText(Path.Combine(fallbackX64, library.Key), "alias-target");
        }
        fixture.Platform.FinalPathOverrides[Path.GetFullPath(fallbackX64)] =
            Path.Combine(fixture.Options.NativeLibraryPath!, "x64");

        Assert.False(fixture.CreateBoundary().TryPrepare(fixture.Options, Log));
        Assert.Empty(fixture.Platform.LoadCalls);
    }

    [Fact]
    public void TryPrepare_DeduplicatesIdenticalFallbackRootsWithoutWeakeningProof()
    {
        var fixture = CreateFixture(allFallbackRootsEqual: true);

        Assert.True(fixture.CreateBoundary().TryPrepare(fixture.Options, Log));

        Assert.Equal(2, fixture.Platform.LoadCalls.Count);
        Assert.Equal(4, fixture.Platform.FileProbeCounts
            .Where(item => item.Key.Contains("fallback-shared", StringComparison.Ordinal))
            .Sum(item => item.Value));
    }

    [Fact]
    public void TryPrepare_RejectsApprovedDirectoryPathAlias()
    {
        var fixture = CreateFixture();
        var approvedX64 = Path.Combine(fixture.Options.NativeLibraryPath!, "x64");
        fixture.Platform.FinalPathOverrides[Path.GetFullPath(approvedX64)] =
            Path.Combine(_root, "different-physical-directory");

        Assert.False(fixture.CreateBoundary().TryPrepare(fixture.Options, Log));
        Assert.Empty(fixture.Platform.LoadCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryPrepare_RejectsWindowsDllRedirectionFileOrDirectoryBeforePreload(
        bool asDirectory)
    {
        var fixture = CreateFixture();
        var redirectionPath = fixture.Platform.ProcessPath + ".local";
        if (asDirectory)
            Directory.CreateDirectory(redirectionPath);
        else
            File.WriteAllText(redirectionPath, string.Empty);

        Assert.False(fixture.CreateBoundary().TryPrepare(fixture.Options, Log));
        Assert.Empty(fixture.Platform.LoadCalls);
    }

    [Fact]
    public void TryPrepare_RejectsPreexistingOcrModuleWithoutExecutingAnotherLoad()
    {
        var fixture = CreateFixture();
        fixture.Platform.LoadedModules.Add(new LoadedOcrModule(
            TesseractNativeLoadBoundary.TesseractFileName,
            Path.Combine(_root, "attacker", TesseractNativeLoadBoundary.TesseractFileName)));

        Assert.False(fixture.CreateBoundary().TryPrepare(fixture.Options, Log));
        Assert.Empty(fixture.Platform.LoadCalls);
    }

    [Fact]
    public void TryPrepare_RejectsPreloadedPinnedDependencyOutsideSystem32()
    {
        var fixture = CreateFixture();
        var dependency = TesseractNativeLoadBoundary.SystemDependencyFileNames[0];
        var index = fixture.Platform.LoadedModules.FindIndex(module =>
            string.Equals(module.Name, dependency, StringComparison.OrdinalIgnoreCase));
        fixture.Platform.LoadedModules[index] = new LoadedOcrModule(
            dependency,
            Path.Combine(fixture.Platform.BaseDirectory, dependency));

        Assert.False(fixture.CreateBoundary().TryPrepare(fixture.Options, Log));
        Assert.Empty(fixture.Platform.LoadCalls);
    }

    [Fact]
    public void TryPrepare_RejectsDuplicateLoadedModuleIdentityAndReleasesPreloads()
    {
        var fixture = CreateFixture();
        fixture.Platform.DuplicateTesseractAfterLoad = true;

        Assert.False(fixture.CreateBoundary().TryPrepare(fixture.Options, Log));

        Assert.Equal(2, fixture.Platform.LoadCalls.Count);
        Assert.Equal(2, fixture.Platform.FreeCalls.Count);
        Assert.Null(fixture.Platform.WrapperSearchPath);
    }

    [Fact]
    public void TryPrepare_RejectsModulePathAliasAndReleasesPreloads()
    {
        var fixture = CreateFixture();
        fixture.Platform.ReportedModulePathOverride = path =>
            path.EndsWith(
                TesseractNativeLoadBoundary.TesseractFileName,
                StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(
                    fixture.Platform.BaseDirectory,
                    "x64",
                    TesseractNativeLoadBoundary.TesseractFileName)
                : path;

        Assert.False(fixture.CreateBoundary().TryPrepare(fixture.Options, Log));

        Assert.Equal(2, fixture.Platform.LoadCalls.Count);
        Assert.Equal(2, fixture.Platform.FreeCalls.Count);
        Assert.Equal(0, fixture.Platform.WrapperSearchPathSetCount);
    }

    [Fact]
    public void TryPrepare_PreloadFailureReleasesEarlierHandleAndNeverConfiguresWrapper()
    {
        var fixture = CreateFixture();
        fixture.Platform.FailLoadFileName = TesseractNativeLoadBoundary.TesseractFileName;

        Assert.False(fixture.CreateBoundary().TryPrepare(fixture.Options, Log));

        Assert.Equal(2, fixture.Platform.LoadCalls.Count);
        Assert.Single(fixture.Platform.FreeCalls);
        Assert.Equal(0, fixture.Platform.WrapperSearchPathSetCount);
    }

    [Fact]
    public void TryPrepare_WrapperSearchPathFailureReleasesBothHandles()
    {
        var fixture = CreateFixture();
        fixture.Platform.FailWrapperSearchPath = true;

        Assert.False(fixture.CreateBoundary().TryPrepare(fixture.Options, Log));

        Assert.Equal(2, fixture.Platform.LoadCalls.Count);
        Assert.Equal(2, fixture.Platform.FreeCalls.Count);
        Assert.Null(fixture.Platform.WrapperSearchPath);
    }

    [Fact]
    public void TryPrepare_WrapperCachePinFailureUnpinsPartialStateAndReleasesPreloads()
    {
        var fixture = CreateFixture();
        fixture.Platform.FailPinFileName = TesseractNativeLoadBoundary.TesseractFileName;

        Assert.False(fixture.CreateBoundary().TryPrepare(fixture.Options, Log));

        Assert.Equal(2, fixture.Platform.LoadCalls.Count);
        Assert.Equal(2, fixture.Platform.PinCalls.Count);
        Assert.Equal(
            [TesseractNativeLoadBoundary.LeptonicaFileName],
            fixture.Platform.UnpinCalls);
        Assert.Equal(2, fixture.Platform.FreeCalls.Count);
        Assert.Null(fixture.Platform.WrapperSearchPath);
    }

    [Fact]
    public void TryPrepare_SuccessPreloadsPinnedImportsInDependencyOrderAndProvesPaths()
    {
        var fixture = CreateFixture();
        IReadOnlyDictionary<string, string>? policyModules = null;
        var boundary = fixture.CreateBoundary(
            verifyLoaded: (_, modules) =>
            {
                policyModules = modules;
                return true;
            });

        Assert.True(boundary.TryPrepare(fixture.Options, Log));

        Assert.Collection(
            fixture.Platform.LoadCalls,
            path => Assert.EndsWith(
                TesseractNativeLoadBoundary.LeptonicaFileName,
                path,
                StringComparison.OrdinalIgnoreCase),
            path => Assert.EndsWith(
                TesseractNativeLoadBoundary.TesseractFileName,
                path,
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(fixture.Options.NativeLibraryPath, fixture.Platform.WrapperSearchPath);
        Assert.NotNull(policyModules);
        Assert.Equal(2, policyModules!.Count);
        Assert.Equal(
            ExpectedLibraryPaths(fixture.Options).ToDictionary(),
            policyModules);
    }

    [Fact]
    public void TryPrepare_AfterActivationReassertsSearchPathAndReprovesWrapperCache()
    {
        var fixture = CreateFixture();
        var boundary = fixture.CreateBoundary();
        Assert.True(boundary.TryPrepare(fixture.Options, Log));
        fixture.Platform.MutateWrapperSearchPathForTest(Path.Combine(_root, "attacker"));
        var pinCount = fixture.Platform.PinCalls.Count;

        Assert.True(boundary.TryPrepare(fixture.Options, Log));

        Assert.Equal(fixture.Options.NativeLibraryPath, fixture.Platform.WrapperSearchPath);
        Assert.Equal(pinCount + 2, fixture.Platform.PinCalls.Count);
        Assert.Equal(2, fixture.Platform.LoadCalls.Count);
    }

    [Fact]
    public void TryRunEngineConstructor_HoldsBoundaryAcrossPinnedPreAndPostProof()
    {
        var fixture = CreateFixture();
        var boundary = fixture.CreateBoundary();
        var constructorCalls = 0;

        var result = boundary.TryRunEngineConstructor(
            fixture.Options,
            Log,
            () =>
            {
                constructorCalls++;
                Assert.Equal(fixture.Options.NativeLibraryPath, fixture.Platform.WrapperSearchPath);
                Assert.Equal(2, fixture.Platform.PinCalls.Count);
                fixture.Platform.MutateWrapperSearchPathForTest(Path.Combine(_root, "mutated"));
            });

        Assert.True(result);
        Assert.Equal(1, constructorCalls);
        Assert.Equal(fixture.Options.NativeLibraryPath, fixture.Platform.WrapperSearchPath);
        Assert.Equal(4, fixture.Platform.PinCalls.Count);
    }

    [Fact]
    public async Task ExtractAsync_BoundaryRejectionNeverConstructsTesseractEngine()
    {
        var engineConstructionCount = 0;
        var options = Options.Create(new AgentOptions
        {
            Vision = new VisionOptions
            {
                Tesseract = new TesseractOptions
                {
                    Enabled = true,
                    MemoryHeadroomBytes = 0,
                },
            },
        });
        await using var extractor = new TesseractScreenExtractor(
            options,
            Log,
            new RejectingNativeBoundary(),
            _ =>
            {
                engineConstructionCount++;
                return null!;
            });

        var result = await extractor.ExtractAsync(
            new ScreenBytes([1], 1, 1, DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, engineConstructionCount);
        Assert.False(extractor.IsReady);
        Assert.Equal(
            VisionRuntimeCodes.OcrCohortVerificationFailed,
            extractor.LastFailureCode);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only; no test correctness depends on deletion.
        }
    }

    private Fixture CreateFixture(bool allFallbackRootsEqual = false)
    {
        Directory.CreateDirectory(_root);
        var cohortRoot = Path.Combine(_root, "approved-cohort");
        var approvedX64 = Path.Combine(cohortRoot, "x64");
        Directory.CreateDirectory(approvedX64);
        foreach (var fileName in new[]
                 {
                     TesseractNativeLoadBoundary.LeptonicaFileName,
                     TesseractNativeLoadBoundary.TesseractFileName,
                 })
        {
            File.WriteAllText(Path.Combine(approvedX64, fileName), "approved");
        }

        var shared = Path.Combine(_root, "fallback-shared");
        var platform = new FakeNativeLoadPlatform
        {
            BaseDirectory = allFallbackRootsEqual ? shared : Path.Combine(_root, "base"),
            TesseractAssemblyDirectory = allFallbackRootsEqual
                ? shared
                : Path.Combine(_root, "assembly"),
            CurrentDirectory = allFallbackRootsEqual ? shared : Path.Combine(_root, "cwd"),
            ProcessPath = Path.Combine(_root, "application", "SuavoAgent.Helper.exe"),
            SystemDirectory = Path.Combine(_root, "system32"),
        };
        Directory.CreateDirectory(platform.BaseDirectory);
        Directory.CreateDirectory(platform.TesseractAssemblyDirectory);
        Directory.CreateDirectory(platform.CurrentDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(platform.ProcessPath)!);
        Directory.CreateDirectory(platform.SystemDirectory);
        foreach (var dependency in TesseractNativeLoadBoundary.SystemDependencyFileNames)
        {
            var dependencyPath = Path.Combine(platform.SystemDirectory, dependency);
            File.WriteAllText(dependencyPath, "trusted-system-module");
            platform.LoadedModules.Add(new LoadedOcrModule(dependency, dependencyPath));
        }

        return new Fixture(
            new TesseractOptions
            {
                Enabled = true,
                NativeLibraryPath = cohortRoot,
                TessdataPath = Path.Combine(cohortRoot, "tessdata"),
                Language = "eng",
            },
            platform);
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ExpectedLibraryPaths(
        TesseractOptions options)
    {
        var x64 = Path.Combine(options.NativeLibraryPath!, "x64");
        return
        [
            KeyValuePair.Create(
                TesseractNativeLoadBoundary.LeptonicaFileName,
                Path.GetFullPath(Path.Combine(
                    x64,
                    TesseractNativeLoadBoundary.LeptonicaFileName))),
            KeyValuePair.Create(
                TesseractNativeLoadBoundary.TesseractFileName,
                Path.GetFullPath(Path.Combine(
                    x64,
                    TesseractNativeLoadBoundary.TesseractFileName))),
        ];
    }

    public enum WrapperFallbackRoot
    {
        Assembly,
        Base,
        BaseBin,
        CurrentDirectory,
    }

    private sealed record Fixture(
        TesseractOptions Options,
        FakeNativeLoadPlatform Platform)
    {
        internal TesseractNativeLoadBoundary CreateBoundary(
            Func<TesseractOptions?, bool>? verifyInstalled = null,
            Func<TesseractOptions?, IReadOnlyDictionary<string, string>?, bool>?
                verifyLoaded = null) =>
            new(
                Platform,
                verifyInstalled ?? (_ => true),
                verifyLoaded ?? ((_, _) => true));
    }

    private sealed class RejectingNativeBoundary : ITesseractNativeLoadBoundary
    {
        public bool TryPrepare(TesseractOptions options, ILogger logger) => false;

        public bool TryRunEngineConstructor(
            TesseractOptions options,
            ILogger logger,
            Action constructEngine) => false;
    }

    private sealed class FakeNativeLoadPlatform : ITesseractNativeLoadPlatform
    {
        private readonly Dictionary<nint, string> _handlePaths = new();
        private int _nextHandle = 1;

        public bool IsWindows => true;
        public required string BaseDirectory { get; init; }
        public required string TesseractAssemblyDirectory { get; init; }
        public required string CurrentDirectory { get; init; }
        public required string ProcessPath { get; init; }
        public required string SystemDirectory { get; init; }
        public Dictionary<string, string> FinalPathOverrides { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> FileProbeCounts { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public List<string> LoadCalls { get; } = [];
        public List<nint> FreeCalls { get; } = [];
        public List<string> PinCalls { get; } = [];
        public List<string> UnpinCalls { get; } = [];
        public List<LoadedOcrModule> LoadedModules { get; } = [];
        public string? FailLoadFileName { get; set; }
        public string? FailPinFileName { get; set; }
        public bool FailWrapperSearchPath { get; set; }
        public bool DuplicateTesseractAfterLoad { get; set; }
        public Func<string, string>? ReportedModulePathOverride { get; set; }
        public Action<string>? OnOperation { get; set; }
        public int WrapperSearchPathSetCount { get; private set; }
        public string? WrapperSearchPath { get; private set; }

        public bool TryGetFileExists(string path, out bool exists)
        {
            OnOperation?.Invoke("file-probe");
            var full = Path.GetFullPath(path);
            FileProbeCounts[full] = FileProbeCounts.GetValueOrDefault(full) + 1;
            exists = File.Exists(full);
            return true;
        }

        public bool TryGetPathExists(string path, out bool exists)
        {
            OnOperation?.Invoke("path-probe");
            var full = Path.GetFullPath(path);
            exists = File.Exists(full) || Directory.Exists(full);
            return true;
        }

        public bool TryGetFinalPath(string path, bool directory, out string finalPath)
        {
            OnOperation?.Invoke("final-path");
            var full = Path.GetFullPath(path);
            if (FinalPathOverrides.TryGetValue(full, out var configured))
            {
                finalPath = Path.GetFullPath(configured);
                return true;
            }

            var exists = directory ? Directory.Exists(full) : File.Exists(full);
            finalPath = full;
            return exists;
        }

        public nint LoadLibraryFromExactDirectory(string absolutePath)
        {
            OnOperation?.Invoke("load-library");
            var full = Path.GetFullPath(absolutePath);
            LoadCalls.Add(full);
            if (string.Equals(
                    Path.GetFileName(full),
                    FailLoadFileName,
                    StringComparison.OrdinalIgnoreCase))
                return nint.Zero;

            var handle = (nint)_nextHandle++;
            _handlePaths[handle] = full;
            LoadedModules.Add(new LoadedOcrModule(Path.GetFileName(full), full));
            if (DuplicateTesseractAfterLoad &&
                string.Equals(
                    Path.GetFileName(full),
                    TesseractNativeLoadBoundary.TesseractFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                LoadedModules.Add(new LoadedOcrModule(
                    Path.GetFileName(full),
                    Path.Combine(BaseDirectory, "duplicate", Path.GetFileName(full))));
            }
            return handle;
        }

        public bool FreeLibrary(nint handle)
        {
            FreeCalls.Add(handle);
            if (!_handlePaths.Remove(handle, out var path)) return false;
            var index = LoadedModules.FindIndex(module =>
                string.Equals(module.Path, path, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) LoadedModules.RemoveAt(index);
            return true;
        }

        public bool TryGetModulePath(nint handle, out string modulePath)
        {
            if (!_handlePaths.TryGetValue(handle, out var path))
            {
                modulePath = string.Empty;
                return false;
            }
            modulePath = ReportedModulePathOverride?.Invoke(path) ?? path;
            return true;
        }

        public bool TryEnumerateLoadedModules(out IReadOnlyList<LoadedOcrModule> modules)
        {
            OnOperation?.Invoke("enumerate-modules");
            modules = LoadedModules.ToArray();
            return true;
        }

        public bool TrySetWrapperSearchPath(string? cohortRoot)
        {
            OnOperation?.Invoke("set-wrapper-path");
            WrapperSearchPathSetCount++;
            if (FailWrapperSearchPath && cohortRoot is not null) return false;
            WrapperSearchPath = cohortRoot;
            return true;
        }

        public bool TryPinWrapperLibrary(string fileName, out nint handle)
        {
            PinCalls.Add(fileName);
            if (string.Equals(fileName, FailPinFileName, StringComparison.OrdinalIgnoreCase))
            {
                handle = nint.Zero;
                return false;
            }
            var loaded = _handlePaths.SingleOrDefault(item => string.Equals(
                Path.GetFileName(item.Value),
                fileName,
                StringComparison.OrdinalIgnoreCase));
            handle = loaded.Key;
            return handle != nint.Zero;
        }

        public bool TryUnpinWrapperLibrary(string fileName)
        {
            UnpinCalls.Add(fileName);
            return true;
        }

        public void MutateWrapperSearchPathForTest(string? path) => WrapperSearchPath = path;
    }
}
