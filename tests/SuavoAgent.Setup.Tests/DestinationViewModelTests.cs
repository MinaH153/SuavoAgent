using SuavoAgent.Setup.Gui.Services;
using SuavoAgent.Setup.Gui.ViewModels;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class DestinationViewModelTests
{
    [Fact]
    public void Install_disabled_when_sql_server_empty()
    {
        var vm = NewVm();
        vm.SqlServer = "";
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
    public void Install_trims_install_path_on_context()
    {
        var ctx = NewContext();
        var vm = new DestinationViewModel(ctx, () => { });
        vm.InstallPath = "  C:\\Custom\\Suavo\\Agent  ";
        vm.SqlServer = "host,49202";

        vm.InstallCommand.Execute(null);

        Assert.Equal(@"C:\Custom\Suavo\Agent", ctx.InstallDir);
    }

    private static DestinationViewModel NewVm() => new(NewContext(), () => { });

    private static InstallContext NewContext() => new(new SetupConfig(
        PharmacyId: "PH-test",
        ApiKey: "test-key",
        CloudUrl: "https://suavollc.com",
        ReleaseTag: "v3.13.6",
        LearningMode: false));
}
