using System.Xml.Linq;

namespace SuavoAgent.Installer.Tests;

public sealed class WixAuthoringTests
{
    private static readonly string InstallerRoot = FindInstallerRoot();
    private static readonly XNamespace Wxs = "http://wixtoolset.org/schemas/v4/wxs";
    private static readonly XNamespace Util = "http://wixtoolset.org/schemas/v4/wxs/util";
    private static readonly XNamespace Bal = "http://wixtoolset.org/schemas/v4/wxs/bal";

    [Fact]
    public void Msi_IsPerMachineX64AndMajorUpgradeSafe()
    {
        var document = Load("SuavoAgent.Msi", "Package.wxs");
        var package = Assert.Single(document.Descendants(Wxs + "Package"));
        Assert.Equal("perMachine", (string?)package.Attribute("Scope"));
        Assert.Equal("$(SuavoAgentVersion)", (string?)package.Attribute("Version"));
        Assert.Equal("*", (string?)package.Attribute("ProductCode"));
        Assert.Equal("{32C06D4D-CFC3-49CB-A6C4-A52E6EFFFBCB}", (string?)package.Attribute("UpgradeCode"));
        var majorUpgrade = Assert.Single(package.Elements(Wxs + "MajorUpgrade"));
        Assert.Equal("afterInstallExecute", (string?)majorUpgrade.Attribute("Schedule"));
        Assert.NotEmpty((string?)majorUpgrade.Attribute("DowngradeErrorMessage") ?? string.Empty);

        var project = Load("SuavoAgent.Msi", "SuavoAgent.Msi.wixproj");
        Assert.Equal("WixToolset.Sdk/7.0.0", (string?)project.Root?.Attribute("Sdk"));
        Assert.Contains(
            project.Descendants("InstallerPlatform"),
            element => element.Value == "x64");
        Assert.DoesNotContain(project.Descendants("AcceptEula"), static _ => true);
    }

    [Fact]
    public void Msi_UsesNativeServiceTablesAndAuditedHardeningAction()
    {
        var document = Load("SuavoAgent.Msi", "Package.wxs");
        var services = document.Descendants(Wxs + "ServiceInstall").ToArray();
        Assert.Equal(
            ["SuavoAgent.Core", "SuavoAgent.Broker", "SuavoAgent.Watchdog"],
            services.Select(service => (string)service.Attribute("Name")!));
        Assert.Equal("NT AUTHORITY\\LocalService", (string?)services[0].Attribute("Account"));
        Assert.Equal("LocalSystem", (string?)services[1].Attribute("Account"));
        Assert.Equal("LocalSystem", (string?)services[2].Attribute("Account"));
        Assert.All(services, service =>
        {
            Assert.Equal("auto", (string?)service.Attribute("Start"));
            Assert.Empty(service.Elements(Wxs + "ServiceConfig"));
            Assert.Single(service.Elements(Util + "ServiceConfig"));
            Assert.Single(service.Elements(Wxs + "PermissionEx"));
        });
        var controls = document.Descendants(Wxs + "ServiceControl").ToArray();
        Assert.Equal(3, controls.Length);
        Assert.All(controls, control =>
        {
            Assert.Equal("install", (string?)control.Attribute("Start"));
            Assert.Equal("both", (string?)control.Attribute("Stop"));
            Assert.Equal("uninstall", (string?)control.Attribute("Remove"));
            Assert.Equal("yes", (string?)control.Attribute("Wait"));
        });
        var broker = Assert.Single(
            services,
            service => (string?)service.Attribute("Name") == "SuavoAgent.Broker");
        Assert.Equal(
            "SuavoAgent.Core",
            (string?)Assert.Single(broker.Elements(Wxs + "ServiceDependency")).Attribute("Id"));

        var actions = document.Descendants(Wxs + "CustomAction")
            .ToDictionary(action => (string)action.Attribute("Id")!, StringComparer.Ordinal);
        Assert.Equal(4, actions.Count);
        AssertHardeningAction(
            actions["RollbackServiceHardening"],
            "--msi-rollback-service-hardening",
            "rollback");
        AssertHardeningAction(
            actions["ApplyServiceHardening"],
            "--msi-apply-service-hardening",
            "deferred");
        AssertHardeningAction(
            actions["CommitServiceHardening"],
            "--msi-commit-service-hardening",
            "commit");
        var legacyRetirement = actions["RetireLegacyInteractiveLaunch"];
        Assert.Equal("MaintenanceExecutable", (string?)legacyRetirement.Attribute("FileRef"));
        Assert.Equal(
            "--msi-retire-legacy-interactive",
            (string?)legacyRetirement.Attribute("ExeCommand"));
        Assert.Equal("commit", (string?)legacyRetirement.Attribute("Execute"));
        Assert.Equal("yes", (string?)legacyRetirement.Attribute("Impersonate"));
        Assert.Equal("check", (string?)legacyRetirement.Attribute("Return"));
        Assert.Equal("yes", (string?)legacyRetirement.Attribute("HideTarget"));

        var sequence = document.Descendants(Wxs + "Custom")
            .ToDictionary(action => (string)action.Attribute("Action")!, StringComparer.Ordinal);
        Assert.Equal(4, sequence.Count);
        Assert.Equal("ApplyServiceHardening", (string?)sequence["RollbackServiceHardening"].Attribute("Before"));
        Assert.Equal("InstallServices", (string?)sequence["ApplyServiceHardening"].Attribute("After"));
        Assert.Equal("ApplyServiceHardening", (string?)sequence["CommitServiceHardening"].Attribute("After"));
        Assert.Equal(
            "CommitServiceHardening",
            (string?)sequence["RetireLegacyInteractiveLaunch"].Attribute("After"));
        Assert.All(sequence.Values, scheduled => Assert.Equal(
            "NOT REMOVE~=\"ALL\" AND (NOT Installed OR REINSTALL)",
            (string?)scheduled.Attribute("Condition")));

        Assert.Contains(
            document.Descendants(Wxs + "Launch"),
            launch => (string?)launch.Attribute("Condition") ==
                      "REMOVE~=\"ALL\" OR NOT RollbackDisabled");
    }

    [Fact]
    public void Msi_EncodesExactLeastPrivilegeBoundaries()
    {
        var document = Load("SuavoAgent.Msi", "Package.wxs");
        var text = document.ToString();
        Assert.Contains("S-1-5-80-3161787503-2860973704-3751597344-303720228-1013404410", text);
        Assert.Contains("(A;;0x1200a9;;;BU)", text);
        Assert.Contains("(A;OICI;0x1301bf;;;BU)", text);
        Assert.DoesNotContain("(A;OICI;FA;;;BU)", text, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;WD)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GenericAll=\"yes\"", text, StringComparison.Ordinal);

        var installFolderSddl = document.Descendants(Wxs + "Component")
            .Single(component => (string?)component.Attribute("Id") == "InstallDirectoryPermissions")
            .Descendants(Wxs + "PermissionEx")
            .Single()
            .Attribute("Sddl")!.Value;
        Assert.Contains("(A;;0x1200a9;;;BU)", installFolderSddl, StringComparison.Ordinal);
        Assert.DoesNotContain("(A;OICI;0x1200a9;;;BU)", installFolderSddl, StringComparison.Ordinal);

        var dataFolders = document.Descendants(Wxs + "Component")
            .Single(component => (string?)component.Attribute("Id") == "DataDirectoryPermissions")
            .Elements(Wxs + "CreateFolder")
            .ToDictionary(folder => (string)folder.Attribute("Directory")!, StringComparer.Ordinal);
        foreach (var protectedRoot in new[] { "DATAFOLDER", "LOGSFOLDER", "DIAGNOSTICSFOLDER" })
        {
            var sddl = dataFolders[protectedRoot].Element(Wxs + "PermissionEx")!.Attribute("Sddl")!.Value;
            Assert.Contains("(A;;0x1200a9;;;BU)", sddl, StringComparison.Ordinal);
            Assert.DoesNotContain("(A;OICI;0x1200a9;;;BU)", sddl, StringComparison.Ordinal);
            Assert.DoesNotContain("(A;OICI;0x1301bf;;;BU)", sddl, StringComparison.Ordinal);
        }
        foreach (var interactiveRoot in new[]
                 {
                     "HELPERLOGFOLDER",
                     "HELPERDIAGNOSTICSFOLDER",
                     "HONEYTOKENFOLDER",
                 })
        {
            var sddl = dataFolders[interactiveRoot].Element(Wxs + "PermissionEx")!.Attribute("Sddl")!.Value;
            Assert.Contains("(A;OICI;0x1301bf;;;BU)", sddl, StringComparison.Ordinal);
        }

        var helperSddl = document.Descendants(Wxs + "File")
            .Single(file => (string?)file.Attribute("Id") == "HelperExecutable")
            .Element(Wxs + "PermissionEx")!
            .Attribute("Sddl")!.Value;
        Assert.StartsWith("D:P", helperSddl, StringComparison.Ordinal);
        Assert.Contains("(A;;0x1200a9;;;BU)", helperSddl, StringComparison.Ordinal);
    }

    [Fact]
    public void Msi_InstallsExactFiveExecutableCohortAndIntegrityRoot()
    {
        var document = Load("SuavoAgent.Msi", "Package.wxs");
        var installedNames = document.Descendants(Wxs + "File")
            .Select(file => (string)file.Attribute("Name")!)
            .Where(static name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(
            [
                "SuavoAgent.Core.exe",
                "SuavoAgent.Broker.exe",
                "SuavoAgent.Watchdog.exe",
                "SuavoAgent.Helper.exe",
                "SuavoAgent.Maintenance.exe",
            ],
            installedNames);
        Assert.Contains(
            document.Descendants(Wxs + "File"),
            file => (string?)file.Attribute("Name") == "binaries.manifest");
        Assert.Contains(
            document.Descendants(Wxs + "File"),
            file => (string?)file.Attribute("Name") == "install-state.json");
    }

    [Fact]
    public void NativeOnboarding_UsesInstalledElevatedHostWithoutShellOrCredentials()
    {
        var msi = Load("SuavoAgent.Msi", "Package.wxs");
        var component = Assert.Single(
            msi.Descendants(Wxs + "Component"),
            value => (string?)value.Attribute("Id") == "NativeOnboardingEntry");
        var shortcut = Assert.Single(component.Elements(Wxs + "Shortcut"));
        Assert.Equal("Connect SuavoAgent", (string?)shortcut.Attribute("Name"));
        Assert.Equal("[#MaintenanceExecutable]", (string?)shortcut.Attribute("Target"));
        Assert.Equal("--connect-installed", (string?)shortcut.Attribute("Arguments"));
        Assert.Equal("INSTALLFOLDER", (string?)shortcut.Attribute("WorkingDirectory"));
        Assert.DoesNotContain("cmd", shortcut.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", shortcut.ToString(), StringComparison.OrdinalIgnoreCase);
        var registry = Assert.Single(component.Elements(Wxs + "RegistryValue"));
        Assert.Equal("HKLM", (string?)registry.Attribute("Root"));
        Assert.Equal(
            "SOFTWARE\\MKM Technologies LLC\\SuavoAgent",
            (string?)registry.Attribute("Key"));
        Assert.Equal("MaintenancePath", (string?)registry.Attribute("Name"));
        Assert.Equal("[#MaintenanceExecutable]", (string?)registry.Attribute("Value"));

        var bundle = Load("SuavoAgent.Bundle", "Bundle.wxs");
        var application = Assert.Single(
            bundle.Descendants(Bal + "WixStandardBootstrapperApplication"));
        Assert.Equal(
            "[ProgramFiles64Folder]Suavo\\Agent\\SuavoAgent.Maintenance.exe",
            (string?)application.Attribute("LaunchTarget"));
        Assert.Equal("--connect-installed", (string?)application.Attribute("LaunchArguments"));
        Assert.Equal(
            "SuavoAgentMaintenanceElevated",
            (string?)application.Attribute("LaunchTargetElevatedId"));
        var approved = Assert.Single(bundle.Descendants(Wxs + "ApprovedExeForElevation"));
        Assert.Equal("SuavoAgentMaintenanceElevated", (string?)approved.Attribute("Id"));
        Assert.Equal("MaintenancePath", (string?)approved.Attribute("Value"));
        Assert.Equal("always64", (string?)approved.Attribute("Bitness"));
    }

    [Fact]
    public void Msi_PreservesRegulatedDataButRemovesProgramFilesAndLegacyScripts()
    {
        var document = Load("SuavoAgent.Msi", "Package.wxs");
        var dataPermissions = Assert.Single(
            document.Descendants(Wxs + "Component"),
            component => (string?)component.Attribute("Id") == "DataDirectoryPermissions");
        Assert.Equal("yes", (string?)dataPermissions.Attribute("Permanent"));
        Assert.Empty(dataPermissions.Descendants(Wxs + "RemoveFile"));
        var installPermissions = Assert.Single(
            document.Descendants(Wxs + "Component"),
            component => (string?)component.Attribute("Id") == "InstallDirectoryPermissions");
        Assert.DoesNotContain(
            document.Descendants(Wxs + "RemoveFile"),
            file => (string?)file.Attribute("Name") == "*");
        var legacyNames = document.Descendants(Wxs + "RemoveFile")
            .Select(file => (string?)file.Attribute("Name"))
            .ToArray();
        Assert.Contains("bootstrap.ps1", legacyNames);
        Assert.Contains("install.ps1", legacyNames);
        Assert.Contains("quick-install.ps1", legacyNames);
        Assert.Contains("setup-windows.ps1", legacyNames);
        Assert.Contains("suavo-check.ps1", legacyNames);
        Assert.Single(document.Descendants(Wxs + "RemoveRegistryKey"));

        var bootstrap = Assert.Single(
            document.Descendants(Wxs + "Component"),
            component => (string?)component.Attribute("Id") == "BootstrapConfiguration");
        Assert.Equal("yes", (string?)bootstrap.Attribute("NeverOverwrite"));
        Assert.NotEqual("yes", (string?)bootstrap.Attribute("Permanent"));
        var integrity = Assert.Single(
            document.Descendants(Wxs + "Component"),
            component => (string?)component.Attribute("Id") == "IntegrityMetadata");
        Assert.NotEqual("yes", (string?)integrity.Attribute("Permanent"));
    }

    [Fact]
    public void Bundle_IsX64AndPinsTheOnlyExternalPrerequisite()
    {
        var document = Load("SuavoAgent.Bundle", "Bundle.wxs");
        var bundle = Assert.Single(document.Descendants(Wxs + "Bundle"));
        Assert.Equal("VersionNT64 >= v10.0", (string?)bundle.Attribute("Condition"));
        Assert.Equal("$(SuavoAgentVersion)", (string?)bundle.Attribute("Version"));
        Assert.Single(bundle.Descendants(Bal + "WixStandardBootstrapperApplication"));
        var redist = Assert.Single(bundle.Descendants(Wxs + "ExePackage"));
        Assert.Equal("yes", (string?)redist.Attribute("PerMachine"));
        Assert.Equal("yes", (string?)redist.Attribute("Permanent"));
        Assert.Equal("yes", (string?)redist.Attribute("Vital"));
        var detectCondition = (string?)redist.Attribute("DetectCondition") ?? string.Empty;
        Assert.Contains("VCRedistInstalled = 1", detectCondition, StringComparison.Ordinal);
        Assert.Contains("VCRedistMajor", detectCondition, StringComparison.Ordinal);
        Assert.Contains("VCRedistMinor", detectCondition, StringComparison.Ordinal);
        Assert.Contains("VCRedistBuild", detectCondition, StringComparison.Ordinal);
        Assert.Contains("VCRedistRevision", detectCondition, StringComparison.Ordinal);
        Assert.Equal(5, document.Descendants(Util + "RegistrySearch").Count());
        var searches = document.Descendants(Util + "RegistrySearch")
            .ToDictionary(search => (string)search.Attribute("Variable")!, StringComparer.Ordinal);
        Assert.Equal("Installed", (string?)searches["VCRedistInstalled"].Attribute("Value"));
        Assert.Equal("Major", (string?)searches["VCRedistMajor"].Attribute("Value"));
        Assert.Equal("Minor", (string?)searches["VCRedistMinor"].Attribute("Value"));
        Assert.Equal("Bld", (string?)searches["VCRedistBuild"].Attribute("Value"));
        Assert.Equal("Rbld", (string?)searches["VCRedistRevision"].Attribute("Value"));
        Assert.All(searches.Values, search =>
        {
            Assert.Equal("always64", (string?)search.Attribute("Bitness"));
            Assert.Equal("value", (string?)search.Attribute("Result"));
        });
        var msi = Assert.Single(bundle.Descendants(Wxs + "MsiPackage"));
        Assert.Equal("$(var.SuavoAgent.Msi.TargetPath)", (string?)msi.Attribute("SourceFile"));

        var project = Load("SuavoAgent.Bundle", "SuavoAgent.Bundle.wixproj");
        Assert.Equal("WixToolset.Sdk/7.0.0", (string?)project.Root?.Attribute("Sdk"));
        Assert.Contains(
            project.Descendants("ProjectReference"),
            reference => ((string?)reference.Attribute("Include"))?
                .EndsWith("SuavoAgent.Msi.wixproj", StringComparison.Ordinal) == true);
        Assert.Contains(
            project.Descendants("DefineConstants"),
            constants => constants.Value.Contains(
                "SuavoAgentVersion=$(SuavoAgentVersion)",
                StringComparison.Ordinal));
        Assert.Contains(
            project.Descendants("VCRedistSha256"),
            element => element.Value == "cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b");
        Assert.Contains(project.Descendants("VCRedistMinimumMajor"), element => element.Value == "14");
        Assert.Contains(project.Descendants("VCRedistMinimumMinor"), element => element.Value == "44");
        Assert.Contains(project.Descendants("VCRedistMinimumBuild"), element => element.Value == "35211");
        Assert.Contains(project.Descendants("VCRedistMinimumRevision"), element => element.Value == "0");
        Assert.Single(project.Descendants("VerifyFileHash"));
    }

    [Fact]
    public void InstallerProjects_HavePreEulaRestoreAndEvaluationProbe()
    {
        var projects = new[]
        {
            Load("SuavoAgent.Msi", "SuavoAgent.Msi.wixproj"),
            Load("SuavoAgent.Bundle", "SuavoAgent.Bundle.wixproj"),
        };
        foreach (var project in projects)
        {
            Assert.Contains(
                project.Descendants("Target"),
                target => (string?)target.Attribute("Name") == "EvaluateInstallerProject");
            Assert.DoesNotContain(
                project.Descendants("Error"),
                error => ((string?)error.Attribute("Condition"))?.StartsWith(
                    "'$([System.Text.RegularExpressions.Regex]::IsMatch",
                    StringComparison.Ordinal) == true);
            Assert.DoesNotContain(project.Descendants("AcceptEula"), static _ => true);
        }

        var preflight = Load("SuavoAgent.Installer.Preflight.proj");
        var projectIncludes = preflight.Descendants("InstallerProject")
            .Select(element => (string?)element.Attribute("Include"))
            .ToArray();
        Assert.Equal(2, projectIncludes.Length);
        Assert.Contains(projectIncludes, include => include?.EndsWith("SuavoAgent.Msi.wixproj") == true);
        Assert.Contains(projectIncludes, include => include?.EndsWith("SuavoAgent.Bundle.wixproj") == true);
        Assert.DoesNotContain(preflight.Descendants("AcceptEula"), static _ => true);
    }

    [Fact]
    public void InstallerTree_HasNoScriptOrAdHocServiceRegistration()
    {
        var files = Directory.EnumerateFiles(InstallerRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(path => !path.EndsWith("WixAuthoringTests.cs", StringComparison.Ordinal))
            .ToArray();
        Assert.DoesNotContain(
            files,
            path => path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase));
        var executableAuthoring = files.Where(path =>
            path.EndsWith(".wxs", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".wixproj", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        var text = string.Join("\n", executableAuthoring.Select(File.ReadAllText));
        Assert.DoesNotContain("sc.exe", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", text, StringComparison.OrdinalIgnoreCase);
    }

    private static XDocument Load(params string[] path) =>
        XDocument.Load(Path.Combine([InstallerRoot, .. path]));

    private static void AssertHardeningAction(
        XElement action,
        string command,
        string execution)
    {
        Assert.Equal("MaintenanceExecutable", (string?)action.Attribute("FileRef"));
        Assert.Equal(command, (string?)action.Attribute("ExeCommand"));
        Assert.Equal(execution, (string?)action.Attribute("Execute"));
        Assert.Equal("no", (string?)action.Attribute("Impersonate"));
        Assert.Equal("check", (string?)action.Attribute("Return"));
        Assert.Equal("yes", (string?)action.Attribute("HideTarget"));
    }

    private static string FindInstallerRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "installer");
            if (File.Exists(Path.Combine(candidate, "SuavoAgent.Msi", "Package.wxs")))
                return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate installer source root.");
    }
}
