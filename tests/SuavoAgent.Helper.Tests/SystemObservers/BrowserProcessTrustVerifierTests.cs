using System.Security.AccessControl;
using SuavoAgent.Helper.SystemObservers.BrowserConnector;
using Xunit;

namespace SuavoAgent.Helper.Tests.SystemObservers;

public sealed class BrowserProcessTrustVerifierTests
{
    [Fact]
    public void ProtectedExactMachinePath_IsAllowedThroughInjectedEvidence()
    {
        var authorization = ChromeAuthorization(BrowserConnectorAuthorityTests.ChromePath);
        var system = new FakeProcessTrustSystem
        {
            ProcessPath = BrowserConnectorAuthorityTests.ChromePath,
            AuthorizedCanonicalPath = BrowserConnectorAuthorityTests.ChromePath,
        };

        var trusted = WindowsBrowserProcessTrustVerifier.Verify(
            42,
            authorization,
            system);

        Assert.True(trusted);
        Assert.Equal(1, system.LocationChecks);
        Assert.Equal(1, system.ReparseChecks);
        Assert.Equal(1, system.AclChecks);
        Assert.Equal(1, system.FileIdentityChecks);
    }

    [Fact]
    public void CopiedSignedChromeInUserWritablePath_IsRejected()
    {
        const string copied =
            @"C:\Users\alice\AppData\Local\CopiedChrome\chrome.exe";
        var authorization = ChromeAuthorization(copied);
        var system = new FakeProcessTrustSystem
        {
            ProcessPath = copied,
            AuthorizedCanonicalPath = copied,
            MachineVendorPath = false,
            FileIdentity = true,
        };

        Assert.True(BrowserExecutablePathPolicy.IsValidAuthorityPath(
            copied,
            BrowserFamily.Chrome));
        Assert.False(WindowsBrowserProcessTrustVerifier.Verify(
            42,
            authorization,
            system));
        Assert.Equal(1, system.LocationChecks);
        Assert.Equal(0, system.FileIdentityChecks);
    }

    [Fact]
    public void ProcessImageDifferentFromSignedExactPath_IsRejectedBeforeLocationOrAcl()
    {
        const string signed =
            @"C:\Program Files\Google\Chrome Beta\Application\chrome.exe";
        var system = new FakeProcessTrustSystem
        {
            ProcessPath = BrowserConnectorAuthorityTests.ChromePath,
            AuthorizedCanonicalPath = signed,
        };

        var trusted = WindowsBrowserProcessTrustVerifier.Verify(
            42,
            ChromeAuthorization(signed),
            system);

        Assert.False(trusted);
        Assert.Equal(0, system.LocationChecks);
        Assert.Equal(0, system.AclChecks);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ReparseOrWritableAclEvidence_IsRejected(
        bool noReparse,
        bool protectedAcl)
    {
        var system = new FakeProcessTrustSystem
        {
            ProcessPath = BrowserConnectorAuthorityTests.ChromePath,
            AuthorizedCanonicalPath = BrowserConnectorAuthorityTests.ChromePath,
            NoReparse = noReparse,
            ProtectedAcl = protectedAcl,
        };

        Assert.False(WindowsBrowserProcessTrustVerifier.Verify(
            42,
            ChromeAuthorization(BrowserConnectorAuthorityTests.ChromePath),
            system));
    }

    [Theory]
    [InlineData(
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        BrowserFamily.Chrome,
        true)]
    [InlineData(
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        BrowserFamily.Edge,
        true)]
    [InlineData(
        @"C:\Users\alice\AppData\Local\Google\Chrome\Application\chrome.exe",
        BrowserFamily.Chrome,
        false)]
    [InlineData(
        @"C:\Program Files Evil\Google\Chrome\Application\chrome.exe",
        BrowserFamily.Chrome,
        false)]
    public void MachineVendorPolicy_HasExactRootBoundary(
        string path,
        BrowserFamily browser,
        bool expected)
    {
        Assert.Equal(expected,
            BrowserExecutablePathPolicy.IsUnderExpectedMachineVendorPath(
                path,
                browser,
                @"C:\Program Files",
                @"C:\Program Files (x86)"));
    }

    [Theory]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-11")]
    [InlineData("S-1-5-32-545")]
    [InlineData("S-1-5-21-1000-1000-1000-1001")]
    [InlineData("S-1-5-21-9999-9999-9999-9999")]
    public void WritableGrantToAnyNonAdminPrincipal_IsRejected(string sid)
    {
        var chain = RealisticProgramFilesChain();
        chain[1] = chain[1] with
        {
            Rules =
            [
                .. chain[1].Rules,
                Rule(
                    sid,
                    BrowserAclRuleEffect.Allow,
                    SpecificWriteMask),
            ],
        };

        Assert.False(BrowserExecutableAclPolicy.IsProtectedChain(chain));
    }

    [Theory]
    [InlineData("S-1-5-32-545", BrowserExecutableAclPolicy.GenericWrite)]
    [InlineData("S-1-5-11", BrowserExecutableAclPolicy.GenericAll)]
    public void GenericWriteOrAllForUsersAndAuthenticatedUsers_IsRejected(
        string sid,
        uint accessMask)
    {
        var chain = RealisticProgramFilesChain();
        chain[2] = chain[2] with
        {
            Rules =
            [
                .. chain[2].Rules,
                Rule(sid, BrowserAclRuleEffect.Allow, accessMask),
            ],
        };

        Assert.False(BrowserExecutableAclPolicy.IsProtectedChain(chain));
    }

    [Theory]
    [InlineData(BrowserFamily.Chrome)]
    [InlineData(BrowserFamily.Edge)]
    public void RealisticDefaultProgramFilesAcl_AllowsMachineBrowser(
        BrowserFamily browser)
    {
        var chain = RealisticProgramFilesChain();

        Assert.True(Enum.IsDefined(browser));
        Assert.True(BrowserExecutableAclPolicy.IsProtectedChain(chain));
    }

    [Fact]
    public void ProgramFilesCreatorOwnerInheritOnlyFullControl_DoesNotApplyToRoot()
    {
        var chain = RealisticProgramFilesChain();
        var creatorOwner = Assert.Single(
            chain[0].Rules,
            rule => rule.IdentitySid == "S-1-3-0");

        Assert.True(creatorOwner.ContainerInherit);
        Assert.True(creatorOwner.ObjectInherit);
        Assert.True(creatorOwner.InheritOnly);
        Assert.Equal(BrowserExecutableAclPolicy.GenericAll, creatorOwner.AccessMask);
        Assert.True(BrowserExecutableAclPolicy.IsProtectedChain(chain));
    }

    [Fact]
    public void InheritOnlyNoPropagateWrite_DoesNotApplyToCurrentDirectory()
    {
        var chain = RealisticProgramFilesChain();
        chain[0] = chain[0] with
        {
            Rules =
            [
                .. chain[0].Rules,
                Rule(
                    "S-1-5-11",
                    BrowserAclRuleEffect.Allow,
                    BrowserExecutableAclPolicy.GenericAll,
                    containerInherit: true,
                    inheritOnly: true,
                    noPropagateInherit: true),
            ],
        };

        Assert.True(BrowserExecutableAclPolicy.IsProtectedChain(chain));
    }

    [Fact]
    public void InheritOnlyWriteWithoutPropagationTarget_FailsClosed()
    {
        var chain = RealisticProgramFilesChain();
        chain[0] = chain[0] with
        {
            Rules =
            [
                .. chain[0].Rules,
                Rule(
                    "S-1-5-11",
                    BrowserAclRuleEffect.Allow,
                    BrowserExecutableAclPolicy.GenericAll,
                    inheritOnly: true),
            ],
        };

        Assert.False(BrowserExecutableAclPolicy.IsProtectedChain(chain));
    }

    [Fact]
    public void UnknownEffectiveWriteAce_FailsClosed()
    {
        var chain = RealisticProgramFilesChain();
        chain[1] = chain[1] with
        {
            Rules =
            [
                .. chain[1].Rules,
                Rule(
                    string.Empty,
                    BrowserAclRuleEffect.Unknown,
                    BrowserExecutableAclPolicy.GenericWrite),
            ],
        };

        Assert.False(BrowserExecutableAclPolicy.IsProtectedChain(chain));
    }

    [Fact]
    public void NonAdminOwner_IsRejectedEvenWithReadOnlyDacl()
    {
        var chain = RealisticProgramFilesChain();
        chain[2] = chain[2] with
        {
            OwnerSid = "S-1-5-21-1000-1000-1000-1001",
        };

        Assert.False(BrowserExecutableAclPolicy.IsProtectedChain(chain));
    }

    private static List<BrowserAclObjectEvidence> RealisticProgramFilesChain()
    {
        var inheritedDirectoryRules = new BrowserAclRuleEvidence[]
        {
            Rule(
                BrowserExecutableAclPolicy.SystemSid,
                BrowserAclRuleEffect.Allow,
                BrowserExecutableAclPolicy.GenericAll,
                isInherited: true,
                containerInherit: true,
                objectInherit: true),
            Rule(
                BrowserExecutableAclPolicy.AdministratorsSid,
                BrowserAclRuleEffect.Allow,
                SpecificWriteMask,
                isInherited: true,
                containerInherit: true,
                objectInherit: true),
            Rule(
                "S-1-5-32-545",
                BrowserAclRuleEffect.Allow,
                unchecked((uint)(int)FileSystemRights.ReadAndExecute),
                isInherited: true,
                containerInherit: true,
                objectInherit: true),
        };
        return
        [
            new(
                BrowserExecutableAclPolicy.TrustedInstallerSid,
                BrowserAclObjectKind.Directory,
                0,
                [
                    .. inheritedDirectoryRules,
                    Rule(
                        "S-1-3-0",
                        BrowserAclRuleEffect.Allow,
                        BrowserExecutableAclPolicy.GenericAll,
                        containerInherit: true,
                        objectInherit: true,
                        inheritOnly: true),
                ]),
            new(
                BrowserExecutableAclPolicy.AdministratorsSid,
                BrowserAclObjectKind.Directory,
                1,
                [.. inheritedDirectoryRules]),
            new(
                BrowserExecutableAclPolicy.AdministratorsSid,
                BrowserAclObjectKind.Directory,
                2,
                [.. inheritedDirectoryRules]),
            new(
                BrowserExecutableAclPolicy.AdministratorsSid,
                BrowserAclObjectKind.File,
                3,
                [
                    Rule(
                        BrowserExecutableAclPolicy.SystemSid,
                        BrowserAclRuleEffect.Allow,
                        BrowserExecutableAclPolicy.GenericAll,
                        isInherited: true),
                    Rule(
                        BrowserExecutableAclPolicy.AdministratorsSid,
                        BrowserAclRuleEffect.Allow,
                        SpecificWriteMask,
                        isInherited: true),
                    Rule(
                        "S-1-5-32-545",
                        BrowserAclRuleEffect.Allow,
                        unchecked((uint)(int)FileSystemRights.ReadAndExecute),
                        isInherited: true),
                ]),
        ];
    }

    private static BrowserAclRuleEvidence Rule(
        string sid,
        BrowserAclRuleEffect effect,
        uint accessMask,
        bool isInherited = false,
        bool containerInherit = false,
        bool objectInherit = false,
        bool inheritOnly = false,
        bool noPropagateInherit = false) => new(
            sid,
            effect,
            accessMask,
            isInherited,
            containerInherit,
            objectInherit,
            inheritOnly,
            noPropagateInherit);

    private const uint SpecificWriteMask = unchecked((uint)(int)(
        FileSystemRights.WriteData |
        FileSystemRights.AppendData |
        FileSystemRights.Delete |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership));

    private static BrowserConnectorAuthorityEntry ChromeAuthorization(string path) => new(
        BrowserFamily.Chrome,
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/",
        path);

    private sealed class FakeProcessTrustSystem : IWindowsBrowserProcessTrustSystem
    {
        public bool IsSupportedPlatform { get; init; } = true;
        public bool ProcessPathAvailable { get; init; } = true;
        public bool AuthorizedPathAvailable { get; init; } = true;
        public string ProcessPath { get; init; } = string.Empty;
        public string AuthorizedCanonicalPath { get; init; } = string.Empty;
        public bool MachineVendorPath { get; init; } = true;
        public bool NoReparse { get; init; } = true;
        public bool ProtectedAcl { get; init; } = true;
        public bool FileIdentity { get; init; } = true;
        public int LocationChecks { get; private set; }
        public int ReparseChecks { get; private set; }
        public int AclChecks { get; private set; }
        public int FileIdentityChecks { get; private set; }

        public bool TryGetCanonicalProcessPath(
            uint processId,
            out string canonicalPath)
        {
            canonicalPath = ProcessPath;
            return ProcessPathAvailable;
        }

        public bool TryCanonicalizeExistingPath(
            string path,
            out string canonicalPath)
        {
            canonicalPath = AuthorizedCanonicalPath;
            return AuthorizedPathAvailable;
        }

        public bool IsExpectedMachineVendorPath(
            string path,
            BrowserFamily browser)
        {
            LocationChecks++;
            return MachineVendorPath;
        }

        public bool HasNoReparseAncestor(string path)
        {
            ReparseChecks++;
            return NoReparse;
        }

        public bool HasProtectedAclChain(string path, BrowserFamily browser)
        {
            AclChecks++;
            return ProtectedAcl;
        }

        public bool HasExpectedFileIdentity(string path, BrowserFamily browser)
        {
            FileIdentityChecks++;
            return FileIdentity;
        }
    }
}
