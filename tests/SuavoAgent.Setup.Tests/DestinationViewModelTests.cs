using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SuavoAgent.Setup.Gui.Services;
using SuavoAgent.Setup.Gui.ViewModels;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class DestinationViewModelTests
{
    [Fact]
    public void Install_blocked_when_sql_is_missing()
    {
        var vm = NewVm();
        vm.SqlServer = "";
        Assert.True(vm.IsSqlMissing);
        Assert.False(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public void Install_blocked_when_install_path_empty()
    {
        var vm = NewVm();
        vm.InstallPath = "";
        vm.SqlServer = "";
        Assert.False(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public void Install_blocked_when_sql_server_set_but_database_missing()
    {
        // Partial SQL config is still a misconfig — if they start entering SQL,
        // it must be complete (no silent half-configured connection).
        var vm = NewVm();
        vm.SqlServer = "host,49202";
        vm.SqlDatabase = "";
        Assert.False(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public void Install_command_cannot_create_a_disconnected_context()
    {
        var ctx = NewContext();
        var vm = new DestinationViewModel(ctx, () => { });
        vm.SqlServer = "";

        Assert.Null(ctx.SqlCredentials);
        Assert.False(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public void Install_disabled_when_sql_auth_checked_but_credentials_empty()
    {
        var vm = NewVm();
        vm.SqlServer = "host,49202";
        vm.SqlDatabase = "PioneerPharmacySystem";
        vm.UseSqlAuth = true;
        Assert.False(vm.InstallCommand.CanExecute(null));

        vm.SqlUser = "sa";
        Assert.False(vm.InstallCommand.CanExecute(null));

        vm.SqlPassword = "pw";
        Assert.True(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public void Install_persists_sql_credentials_to_context_with_windows_auth_when_unchecked()
    {
        var ctx = NewContext();
        var vm = new DestinationViewModel(ctx, () => { });
        vm.SqlServer = "host,49202";
        vm.SqlDatabase = "PioneerPharmacySystem";
        vm.UseSqlAuth = false;

        vm.InstallCommand.Execute(null);

        Assert.NotNull(ctx.SqlCredentials);
        Assert.Equal("host,49202", ctx.SqlCredentials!.Server);
        Assert.True(ctx.SqlCredentials.IsWindowsAuth);
        Assert.Null(ctx.SqlCredentials.User);
    }

    [Fact]
    public void Install_persists_sql_credentials_to_context_with_sql_auth_when_checked()
    {
        var ctx = NewContext();
        var vm = new DestinationViewModel(ctx, () => { });
        vm.SqlServer = "host,49202";
        vm.SqlDatabase = "PioneerPharmacySystem";
        vm.UseSqlAuth = true;
        vm.SqlUser = "suavo_read";
        vm.SqlPassword = "s3cret";

        vm.InstallCommand.Execute(null);

        Assert.Equal("suavo_read", ctx.SqlCredentials!.User);
        Assert.Equal("s3cret", ctx.SqlCredentials.Password);
        Assert.False(ctx.SqlCredentials.IsWindowsAuth);
    }

    [Fact]
    public void Install_rejects_a_custom_path_and_preserves_msi_owned_location()
    {
        var ctx = NewContext();
        var vm = new DestinationViewModel(ctx, () => { });
        vm.InstallPath = "  C:\\Custom\\Suavo\\Agent  ";
        vm.SqlServer = "host,49202";

        Assert.Throws<InvalidOperationException>(() =>
            vm.InstallCommand.Execute(null));
        Assert.Equal(@"C:\Program Files\Suavo\Agent", ctx.InstallDir);
    }

    [Fact]
    public void Install_path_is_always_locked_to_the_msi_owned_location()
    {
        var vm = NewVm();

        Assert.True(vm.IsInstallPathLocked);
        Assert.Equal(@"C:\Program Files\Suavo\Agent", vm.InstallPath);
    }

    [Fact]
    public async Task Valid_public_sql_certificate_is_verified_before_install_and_saved_to_context()
    {
        var source = CreateCertificateFile();
        try
        {
            var ctx = NewContext();
            var vm = new DestinationViewModel(
                ctx,
                () => { },
                () => Task.FromResult<string?>(source));
            vm.SqlServer = "host,49202";

            await vm.SelectSqlCertificateAsync();

            Assert.Equal(Path.GetFullPath(source), vm.SqlCertificatePath);
            Assert.Contains("validated", vm.SqlCertificateStatus);
            Assert.True(vm.InstallCommand.CanExecute(null));
            vm.InstallCommand.Execute(null);
            Assert.Equal(Path.GetFullPath(source), ctx.SqlServerCertificateSourcePath);
        }
        finally
        {
            try { File.Delete(source); } catch { }
        }
    }

    [Fact]
    public async Task Invalid_certificate_blocks_advance_until_operator_clears_or_replaces_it()
    {
        var source = Path.Combine(
            Path.GetTempPath(),
            "suavo-invalid-sql-cert-" + Guid.NewGuid().ToString("N") + ".cer");
        await File.WriteAllTextAsync(source, "not a certificate");
        try
        {
            var ctx = NewContext();
            var vm = new DestinationViewModel(
                ctx,
                () => { },
                () => Task.FromResult<string?>(source));
            vm.SqlServer = "host,49202";

            await vm.SelectSqlCertificateAsync();

            Assert.False(vm.InstallCommand.CanExecute(null));
            Assert.Contains("invalid", vm.SqlCertificateStatus);
            Assert.Null(ctx.SqlServerCertificateSourcePath);
            vm.ClearSqlCertificateCommand.Execute(null);
            Assert.True(vm.InstallCommand.CanExecute(null));
            Assert.False(vm.HasSqlCertificate);
        }
        finally
        {
            try { File.Delete(source); } catch { }
        }
    }

    [Fact]
    public void Destination_ui_exposes_optional_certificate_picker_status_and_clear_action()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/SuavoAgent.Setup/Gui/Views/DestinationView.axaml"));
        var source = File.ReadAllText(path);

        Assert.Contains("SQL server certificate (optional)", source);
        Assert.Contains("SelectSqlCertificateCommand", source);
        Assert.Contains("SqlCertificateStatus", source);
        Assert.Contains("ClearSqlCertificateCommand", source);
        Assert.Contains("IsReadOnly=\"True\"", source);
    }

    private static DestinationViewModel NewVm() => new(NewContext(), () => { });

    private static InstallContext NewContext() => new(new SetupConfig(
        PharmacyId: "PH-test",
        ApiKey: "test-key",
        CloudUrl: "https://suavollc.com",
        ReleaseTag: "v3.13.6",
        LearningMode: false));

    private static string CreateCertificateFile()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=pioneerrx-sql",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        var path = Path.Combine(
            Path.GetTempPath(),
            "suavo-sql-cert-picker-" + Guid.NewGuid().ToString("N") + ".cer");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Cert));
        return path;
    }
}
