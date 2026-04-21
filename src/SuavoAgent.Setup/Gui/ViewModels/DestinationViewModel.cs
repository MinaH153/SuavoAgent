using SuavoAgent.Setup.Gui.Services;

namespace SuavoAgent.Setup.Gui.ViewModels;

internal sealed class DestinationViewModel : ViewModelBase
{
    private readonly InstallContext _ctx;
    private readonly Action _onInstall;

    private string _installPath;
    private string _sqlServer;
    private string _sqlDatabase;
    private bool _useSqlAuth;
    private string _sqlUser = string.Empty;
    private string _sqlPassword = string.Empty;

    public DestinationViewModel(InstallContext ctx, Action onInstall)
    {
        _ctx = ctx;
        _onInstall = onInstall;

        _installPath = ctx.InstallDir;

        // Pre-fill from auto-discovery if it succeeded.
        _sqlServer = ctx.SqlCredentials?.Server ?? string.Empty;
        _sqlDatabase = ctx.SqlCredentials?.Database ?? "PioneerPharmacySystem";
        _useSqlAuth = ctx.SqlCredentials?.IsWindowsAuth == false;
        _sqlUser = ctx.SqlCredentials?.User ?? string.Empty;
        _sqlPassword = ctx.SqlCredentials?.Password ?? string.Empty;

        InstallCommand = new RelayCommand(Install, CanInstall);
    }

    public string InstallPath
    {
        get => _installPath;
        set { if (SetField(ref _installPath, value)) InstallCommand.RaiseCanExecuteChanged(); }
    }

    public string SqlServer
    {
        get => _sqlServer;
        set { if (SetField(ref _sqlServer, value)) InstallCommand.RaiseCanExecuteChanged(); }
    }

    public string SqlDatabase
    {
        get => _sqlDatabase;
        set { if (SetField(ref _sqlDatabase, value)) InstallCommand.RaiseCanExecuteChanged(); }
    }

    public bool UseSqlAuth
    {
        get => _useSqlAuth;
        set
        {
            if (SetField(ref _useSqlAuth, value))
            {
                InstallCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(SqlAuthLabel));
            }
        }
    }

    public string SqlUser
    {
        get => _sqlUser;
        set { if (SetField(ref _sqlUser, value)) InstallCommand.RaiseCanExecuteChanged(); }
    }

    public string SqlPassword
    {
        get => _sqlPassword;
        set { if (SetField(ref _sqlPassword, value)) InstallCommand.RaiseCanExecuteChanged(); }
    }

    public string SqlAuthLabel => _useSqlAuth ? "SQL authentication" : "Windows authentication (pass-through)";

    public RelayCommand InstallCommand { get; }

    private bool CanInstall()
    {
        if (string.IsNullOrWhiteSpace(_installPath)) return false;
        if (string.IsNullOrWhiteSpace(_sqlServer)) return false;
        if (string.IsNullOrWhiteSpace(_sqlDatabase)) return false;
        if (_useSqlAuth && (string.IsNullOrWhiteSpace(_sqlUser) || string.IsNullOrWhiteSpace(_sqlPassword)))
            return false;
        return true;
    }

    private void Install()
    {
        _ctx.InstallDir = _installPath.Trim();
        _ctx.SqlCredentials = new SqlCredentialDiscovery.SqlCredentials(
            Server: _sqlServer.Trim(),
            Database: _sqlDatabase.Trim(),
            User: _useSqlAuth ? _sqlUser.Trim() : null,
            Password: _useSqlAuth ? _sqlPassword : null);
        _onInstall();
    }
}
