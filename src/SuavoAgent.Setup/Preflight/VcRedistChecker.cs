// src/SuavoAgent.Setup/Preflight/VcRedistChecker.cs
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace SuavoAgent.Setup.Preflight;

/// <summary>
/// Detects the Visual C++ 2015-2022 x64 Redistributable. Authoritative signal is the presence of
/// ALL THREE runtime DLLs in System32 — a partial install (e.g. vcruntime140.dll present but
/// vcruntime140_1.dll missing) lets .NET run yet bricks the llama.cpp brain (the Nadim failure).
/// </summary>
public sealed class VcRedistChecker
{
    public static readonly string[] RequiredDlls =
        { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll" };

    private readonly Func<string, bool> _fileExists;
    private readonly Func<string?> _readRegistryVersion;

    public VcRedistChecker(Func<string, bool>? fileExists = null, Func<string?>? readRegistryVersion = null)
    {
        _fileExists = fileExists ?? File.Exists;
        _readRegistryVersion = readRegistryVersion ?? ReadInstalledVersionFromRegistry;
    }

    public VcRedistStatus Check()
    {
        var system32 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        var missing = new List<string>();
        foreach (var dll in RequiredDlls)
            if (!_fileExists(Path.Combine(system32, dll)))
                missing.Add(dll);
        return new VcRedistStatus(missing.Count == 0, missing, _readRegistryVersion());
    }

    private static string? ReadInstalledVersionFromRegistry()
    {
        try
        {
            using var key = RegistryKey
                .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64");
            if (key?.GetValue("Installed") is int installed && installed == 1)
                return key.GetValue("Version") as string;
            return null;
        }
        catch { return null; }
    }
}

public sealed record VcRedistStatus(bool Installed, IReadOnlyList<string> MissingDlls, string? Version);
