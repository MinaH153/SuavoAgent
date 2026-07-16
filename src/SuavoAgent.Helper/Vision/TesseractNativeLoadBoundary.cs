using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using InteropDotNet;
using Microsoft.Win32.SafeHandles;
using Serilog;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Vision;
using Tesseract;

namespace SuavoAgent.Helper.Vision;

internal interface ITesseractNativeLoadBoundary
{
    bool TryVerifyCohort(TesseractOptions options, ILogger logger);
    bool TryPrepare(TesseractOptions options, ILogger logger);
    bool TryRunEngineConstructor(
        TesseractOptions options,
        ILogger logger,
        Action constructEngine);
}

internal sealed record LoadedOcrModule(string Name, string Path);

internal interface ITesseractNativeLoadPlatform
{
    bool IsWindows { get; }
    string BaseDirectory { get; }
    string TesseractAssemblyDirectory { get; }
    string CurrentDirectory { get; }
    string ProcessPath { get; }
    string SystemDirectory { get; }

    bool TryGetFileExists(string path, out bool exists);
    bool TryGetPathExists(string path, out bool exists);
    bool TryGetFinalPath(string path, bool directory, out string finalPath);
    nint LoadLibraryFromExactDirectory(string absolutePath);
    bool FreeLibrary(nint handle);
    bool TryGetModulePath(nint handle, out string modulePath);
    bool TryEnumerateLoadedModules(out IReadOnlyList<LoadedOcrModule> modules);
    bool TrySetWrapperSearchPath(string? cohortRoot);
    bool TryPinWrapperLibrary(string fileName, out nint handle);
    bool TryUnpinWrapperLibrary(string fileName);
}

/// <summary>
/// Exclusive native-code entry for the pinned charlesw/Tesseract 5.2.0
/// managed wrapper. Upstream commit 2c993543f7fa66576a8890a6c4ab053c4598aaed
/// probes, in order, CustomSearchPath, the Tesseract assembly directory,
/// AppDomain base, AppDomain base/bin, and the working directory. Its Windows
/// loader then calls plain LoadLibrary on the discovered path.
///
/// We therefore verify the release manifest first, reject every non-approved
/// wrapper fallback, and preload the two exact wrapper imports by absolute path
/// with LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32. The
/// wrapper can only acquire already-loaded, path-proven handles afterward.
/// </summary>
internal sealed class TesseractNativeLoadBoundary : ITesseractNativeLoadBoundary
{
    internal const string LeptonicaFileName = "leptonica-1.82.0.dll";
    internal const string TesseractFileName = "tesseract50.dll";
    internal const uint ExactDependencySearchFlags = 0x00000100 | 0x00000800;

    // Full concrete import closure from the PE import tables of the exact
    // Tesseract 5.2.0 NuGet x64 DLLs (SHA-256 dfcb3e6e... Leptonica and
    // de4d04ec... Tesseract). Their remaining direct imports are the
    // api-ms-win-crt-{convert,environment,filesystem,heap,math,runtime,stdio,
    // string,time,utility}-l1-1-0 API-set contracts; on supported Windows
    // 10/11 those resolve to the System32 UCRT host, ucrtbase.dll.
    internal static readonly IReadOnlyList<string> SystemDependencyFileNames =
    [
        "GDI32.dll",
        "KERNEL32.dll",
        "MSVCP140.dll",
        "ucrtbase.dll",
        "USER32.dll",
        "VCRUNTIME140.dll",
        "VCRUNTIME140_1.dll",
        "WS2_32.dll",
    ];

    private static readonly string[] WrapperLibraryNames =
    [
        LeptonicaFileName,
        TesseractFileName,
    ];

    private readonly ITesseractNativeLoadPlatform _platform;
    private readonly Func<TesseractOptions?, bool> _verifyInstalled;
    private readonly Func<
        TesseractOptions?,
        IReadOnlyDictionary<string, string>?,
        bool> _verifyLoadedModules;
    private readonly object _gate = new();

    private string? _activatedX64Directory;
    private IReadOnlyList<nint> _retainedHandles = Array.Empty<nint>();

    internal static TesseractNativeLoadBoundary Shared { get; } = new(
        new WindowsTesseractNativeLoadPlatform(),
        TesseractNativeCohortPolicy.VerifyInstalled,
        TesseractNativeCohortPolicy.VerifyLoadedNativeModulePaths);

    internal TesseractNativeLoadBoundary(
        ITesseractNativeLoadPlatform platform,
        Func<TesseractOptions?, bool> verifyInstalled,
        Func<TesseractOptions?, IReadOnlyDictionary<string, string>?, bool> verifyLoadedModules)
    {
        _platform = platform;
        _verifyInstalled = verifyInstalled;
        _verifyLoadedModules = verifyLoadedModules;
    }

    public bool TryVerifyCohort(TesseractOptions options, ILogger logger)
    {
        if (!_platform.IsWindows) return false;

        lock (_gate)
        {
            try
            {
                // Managed-only preflight. This deliberately performs no
                // fallback probes, native loads, wrapper pinning, or module
                // enumeration, so deterministic rejection never competes
                // with the unmanaged watchdog budget.
                return _verifyInstalled(options);
            }
            catch (Exception exception) when (exception is not
                OutOfMemoryException and not StackOverflowException)
            {
                logger.Warning(
                    "Tesseract native cohort verification rejected configuration ({Type})",
                    exception.GetType().Name);
                return false;
            }
        }
    }

    public bool TryPrepare(TesseractOptions options, ILogger logger)
    {
        if (!_platform.IsWindows) return false;

        lock (_gate)
        {
            return TryPrepareUnderLock(options, logger);
        }
    }

    public bool TryRunEngineConstructor(
        TesseractOptions options,
        ILogger logger,
        Action constructEngine)
    {
        if (!_platform.IsWindows) return false;
        lock (_gate)
        {
            if (!TryPrepareUnderLock(options, logger)) return false;
            constructEngine();
            // Reprove while the same gate is held. All production access to
            // the wrapper resolver goes through this boundary, and its own
            // LibraryLoader cache is already pinned to the approved handles.
            return TryPrepareUnderLock(options, logger);
        }
    }

    private bool TryPrepareUnderLock(TesseractOptions options, ILogger logger)
    {
        try
        {
            // This must precede every filesystem fallback probe and every
            // native API call. It re-hashes the complete compiled-policy
            // inventory, including both executable DLLs and traineddata.
            if (!_verifyInstalled(options)) return false;

            if (!TryResolveApprovedPaths(
                    options,
                    out var cohortRoot,
                    out var approvedX64,
                    out var approvedLibraries))
                return false;

            if (!WrapperFallbacksAreAbsent(approvedX64)) return false;

            if (_activatedX64Directory is not null)
            {
                return PathEquals(_activatedX64Directory, approvedX64) &&
                       _platform.TrySetWrapperSearchPath(cohortRoot) &&
                       WrapperCacheIsPinned(approvedLibraries) &&
                       ProveLoadedModuleSet(options, approvedLibraries);
            }

            // A wrapper or third party that reached either OCR DLL first
            // has already made the process-global loader state ambiguous.
            if (!_platform.TryEnumerateLoadedModules(out var existing) ||
                existing.Any(IsOcrModule) ||
                !SystemDependenciesAreTrusted(existing, requireAll: false))
                return false;

            var acquired = new List<nint>(WrapperLibraryNames.Length);
            var wrapperPinned = new List<string>(WrapperLibraryNames.Length);
            var wrapperPathAttempted = false;
            try
            {
                foreach (var library in approvedLibraries)
                {
                    var handle = _platform.LoadLibraryFromExactDirectory(library.Value);
                    if (handle == nint.Zero)
                        return false;
                    acquired.Add(handle);
                    if (
                        !_platform.TryGetModulePath(handle, out var modulePath) ||
                        !PathEquals(modulePath, library.Value))
                        return false;
                }

                // The wrapper expects the cohort root, then appends x64.
                // Setting it only after both safe preloads avoids exposing
                // an unproven path to its process-global singleton.
                wrapperPathAttempted = true;
                if (!_platform.TrySetWrapperSearchPath(cohortRoot))
                    return false;
                foreach (var library in approvedLibraries)
                {
                    if (!_platform.TryPinWrapperLibrary(library.Key, out var wrapperHandle))
                        return false;
                    wrapperPinned.Add(library.Key);
                    if (wrapperHandle == nint.Zero ||
                        !_platform.TryGetModulePath(wrapperHandle, out var wrapperModulePath) ||
                        !PathEquals(wrapperModulePath, library.Value))
                        return false;
                }
                if (!ProveLoadedModuleSet(options, approvedLibraries))
                    return false;

                _retainedHandles = acquired.ToArray();
                acquired.Clear();
                _activatedX64Directory = approvedX64;
                logger.Information(
                    "Tesseract native boundary: exclusive approved-directory preload established");
                return true;
            }
            finally
            {
                if (acquired.Count > 0)
                {
                    try
                    {
                        for (var index = wrapperPinned.Count - 1; index >= 0; index--)
                            _platform.TryUnpinWrapperLibrary(wrapperPinned[index]);
                        if (wrapperPathAttempted)
                            _platform.TrySetWrapperSearchPath(null);
                    }
                    finally
                    {
                        for (var index = acquired.Count - 1; index >= 0; index--)
                            _platform.FreeLibrary(acquired[index]);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not
            OutOfMemoryException and not StackOverflowException)
        {
            logger.Warning(
                "Tesseract native boundary rejected activation ({Type})",
                exception.GetType().Name);
            return false;
        }
    }

    private bool TryResolveApprovedPaths(
        TesseractOptions options,
        out string cohortRoot,
        out string approvedX64,
        out IReadOnlyList<KeyValuePair<string, string>> approvedLibraries)
    {
        cohortRoot = string.Empty;
        approvedX64 = string.Empty;
        approvedLibraries = Array.Empty<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(options.NativeLibraryPath)) return false;

        cohortRoot = NormalizePath(options.NativeLibraryPath);
        approvedX64 = NormalizePath(Path.Combine(cohortRoot, "x64"));

        // Refuse junction/symlink/short-name aliases in the approved root.
        // LoadLibraryEx and the wrapper must observe the identical path string.
        if (!_platform.TryGetFinalPath(approvedX64, directory: true, out var finalX64) ||
            !PathEquals(finalX64, approvedX64))
            return false;

        var libraries = new List<KeyValuePair<string, string>>(WrapperLibraryNames.Length);
        foreach (var fileName in WrapperLibraryNames)
        {
            var path = NormalizePath(Path.Combine(approvedX64, fileName));
            if (!_platform.TryGetFileExists(path, out var exists) || !exists ||
                !_platform.TryGetFinalPath(path, directory: false, out var finalPath) ||
                !PathEquals(finalPath, path))
                return false;
            libraries.Add(KeyValuePair.Create(fileName, path));
        }

        approvedLibraries = libraries;
        return true;
    }

    private bool WrapperFallbacksAreAbsent(string approvedX64)
    {
        if (string.IsNullOrWhiteSpace(_platform.BaseDirectory) ||
            string.IsNullOrWhiteSpace(_platform.TesseractAssemblyDirectory) ||
            string.IsNullOrWhiteSpace(_platform.CurrentDirectory) ||
            string.IsNullOrWhiteSpace(_platform.ProcessPath))
            return false;

        // Windows DLL redirection precedes even an absolute LoadLibraryEx
        // path. The mere presence of <process>.exe.local can redirect the
        // main DLL or any dependency into the application directory, so the
        // boundary refuses activation before a DllMain can run.
        if (!_platform.TryGetPathExists(
                NormalizePath(_platform.ProcessPath + ".local"),
                out var redirectionExists) ||
            redirectionExists)
            return false;

        // Exact order and roots from LibraryLoader.cs at the pinned upstream
        // commit. CustomSearchPath is the approved root and is intentionally
        // excluded from this fallback list.
        var candidates = new[]
        {
            Path.Combine(_platform.TesseractAssemblyDirectory, "x64"),
            Path.Combine(_platform.BaseDirectory, "x64"),
            Path.Combine(_platform.BaseDirectory, "bin", "x64"),
            Path.Combine(_platform.CurrentDirectory, "x64"),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var fullCandidate = NormalizePath(candidate);
            if (!seen.Add(fullCandidate) || PathEquals(fullCandidate, approvedX64))
                continue;

            foreach (var fileName in WrapperLibraryNames)
            {
                var path = Path.Combine(fullCandidate, fileName);
                if (!_platform.TryGetFileExists(path, out var exists) || exists)
                    return false;
            }
        }

        return true;
    }

    private bool ProveLoadedModuleSet(
        TesseractOptions options,
        IReadOnlyList<KeyValuePair<string, string>> approvedLibraries)
    {
        if (!_platform.TryEnumerateLoadedModules(out var allLoaded) ||
            !SystemDependenciesAreTrusted(allLoaded, requireAll: true))
            return false;
        var loaded = allLoaded.Where(IsOcrModule).ToArray();
        if (loaded.Length != approvedLibraries.Count)
            return false;

        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in loaded)
        {
            if (!byName.TryAdd(module.Name, module.Path)) return false;
        }

        foreach (var approved in approvedLibraries)
        {
            if (!byName.TryGetValue(approved.Key, out var actual) ||
                !PathEquals(actual, approved.Value))
                return false;
        }

        return _verifyLoadedModules(options, byName);
    }

    private bool WrapperCacheIsPinned(
        IReadOnlyList<KeyValuePair<string, string>> approvedLibraries)
    {
        foreach (var library in approvedLibraries)
        {
            if (!_platform.TryPinWrapperLibrary(library.Key, out var handle) ||
                handle == nint.Zero ||
                !_platform.TryGetModulePath(handle, out var modulePath) ||
                !PathEquals(modulePath, library.Value))
                return false;
        }
        return true;
    }

    private bool SystemDependenciesAreTrusted(
        IReadOnlyList<LoadedOcrModule> loaded,
        bool requireAll)
    {
        if (string.IsNullOrWhiteSpace(_platform.SystemDirectory)) return false;
        var systemDirectory = NormalizePath(_platform.SystemDirectory);
        if (!_platform.TryGetFinalPath(systemDirectory, directory: true, out var finalSystem) ||
            !PathEquals(finalSystem, systemDirectory))
            return false;

        var expected = new HashSet<string>(
            SystemDependencyFileNames,
            StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in loaded.Where(module => expected.Contains(module.Name)))
        {
            if (!found.Add(module.Name) ||
                !PathEquals(module.Path, Path.Combine(systemDirectory, module.Name)))
                return false;
        }
        return !requireAll || found.Count == expected.Count;
    }

    private static bool IsOcrModule(LoadedOcrModule module) =>
        string.Equals(module.Name, TesseractFileName, StringComparison.OrdinalIgnoreCase) ||
        module.Name.StartsWith("leptonica-", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            NormalizeWindowsDevicePath(NormalizePath(left)),
            NormalizeWindowsDevicePath(NormalizePath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeWindowsDevicePath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[uncPrefix.Length..];
        return path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase)
            ? path[devicePrefix.Length..]
            : path;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsTesseractNativeLoadPlatform : ITesseractNativeLoadPlatform
{
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    public bool IsWindows => OperatingSystem.IsWindows();
    public string BaseDirectory => AppDomain.CurrentDomain.BaseDirectory;
    public string TesseractAssemblyDirectory =>
        Path.GetDirectoryName(typeof(TesseractEngine).Assembly.Location) ?? string.Empty;
    public string CurrentDirectory => Environment.CurrentDirectory;
    public string ProcessPath => Environment.ProcessPath ?? string.Empty;
    public string SystemDirectory => Environment.SystemDirectory;

    public bool TryGetFileExists(string path, out bool exists)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            exists = (attributes & FileAttributes.Directory) == 0;
            return true;
        }
        catch (FileNotFoundException)
        {
            exists = false;
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            exists = false;
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException)
        {
            exists = false;
            return false;
        }
    }

    public bool TryGetPathExists(string path, out bool exists)
    {
        try
        {
            _ = File.GetAttributes(path);
            exists = true;
            return true;
        }
        catch (FileNotFoundException)
        {
            exists = false;
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            exists = false;
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException)
        {
            exists = false;
            return false;
        }
    }

    public bool TryGetFinalPath(string path, bool directory, out string finalPath)
    {
        finalPath = string.Empty;
        using var handle = CreateFileW(
            path,
            0,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            nint.Zero,
            OpenExisting,
            directory ? FileFlagBackupSemantics : 0,
            nint.Zero);
        if (handle.IsInvalid) return false;

        var capacity = 512;
        while (capacity <= 32768)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)capacity, 0);
            if (length == 0) return false;
            if (length < capacity)
            {
                finalPath = buffer.ToString();
                return true;
            }
            capacity = checked((int)length + 1);
        }
        return false;
    }

    public nint LoadLibraryFromExactDirectory(string absolutePath) =>
        LoadLibraryExW(
            absolutePath,
            nint.Zero,
            TesseractNativeLoadBoundary.ExactDependencySearchFlags);

    public bool FreeLibrary(nint handle) => FreeLibraryNative(handle);

    public bool TryGetModulePath(nint handle, out string modulePath)
    {
        modulePath = string.Empty;
        var capacity = 512;
        while (capacity <= 32768)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetModuleFileNameW(handle, buffer, capacity);
            if (length == 0) return false;
            if (length < capacity - 1)
            {
                modulePath = buffer.ToString();
                return true;
            }
            capacity *= 2;
        }
        return false;
    }

    public bool TryEnumerateLoadedModules(out IReadOnlyList<LoadedOcrModule> modules)
    {
        modules = Array.Empty<LoadedOcrModule>();
        try
        {
            using var process = Process.GetCurrentProcess();
            modules = process.Modules
                .Cast<ProcessModule>()
                .Select(module => new LoadedOcrModule(module.ModuleName, module.FileName))
                .ToArray();
            return true;
        }
        catch (Exception exception) when (exception is
            SystemException or InvalidOperationException)
        {
            return false;
        }
    }

    public bool TrySetWrapperSearchPath(string? cohortRoot)
    {
        try
        {
            TesseractEnviornment.CustomSearchPath = cohortRoot;
            return string.Equals(
                TesseractEnviornment.CustomSearchPath,
                cohortRoot,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is
            SystemException or InvalidOperationException)
        {
            return false;
        }
    }

    public bool TryPinWrapperLibrary(string fileName, out nint handle)
    {
        handle = nint.Zero;
        try
        {
            handle = LibraryLoader.Instance.LoadLibrary(fileName, "x64");
            return handle != nint.Zero;
        }
        catch (Exception exception) when (exception is
            SystemException or InvalidOperationException)
        {
            handle = nint.Zero;
            return false;
        }
    }

    public bool TryUnpinWrapperLibrary(string fileName)
    {
        try
        {
            return !LibraryLoader.Instance.IsLibraryLoaded(fileName) ||
                   LibraryLoader.Instance.FreeLibrary(fileName);
        }
        catch (Exception exception) when (exception is
            SystemException or InvalidOperationException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern nint LoadLibraryExW(
        string fileName,
        nint file,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "FreeLibrary", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibraryNative(nint module);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern int GetModuleFileNameW(
        nint module,
        StringBuilder fileName,
        int size);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathSize,
        uint flags);
}
