using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using SuavoAgent.Setup.Gui.Services;
using SuavoAgent.Setup.Security;

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
    private string _sqlCertificatePath = string.Empty;
    private string _sqlCertificateStatus =
        "Optional — select the SQL Server public certificate only for a self-signed deployment.";
    private bool _sqlCertificateSelectionInvalid;
    private readonly Func<Task<string?>> _pickSqlCertificate;

    public DestinationViewModel(
        InstallContext ctx,
        Action onInstall,
        Func<Task<string?>>? pickSqlCertificate = null)
    {
        _ctx = ctx;
        _onInstall = onInstall;

        // Windows Installer owns one machine-wide location. A selectable path
        // makes Programs & Features cleanup ambiguous and can leave a live
        // executable cohort behind after the UI reports success.
        _installPath = UninstallOrchestrator.DefaultInstallDir;

        // Pre-fill from auto-discovery if it succeeded.
        _sqlServer = ctx.SqlCredentials?.Server ?? string.Empty;
        _sqlDatabase = ctx.SqlCredentials?.Database ?? "PioneerPharmacySystem";
        _useSqlAuth = ctx.SqlCredentials?.IsWindowsAuth == false;
        _sqlUser = ctx.SqlCredentials?.User ?? string.Empty;
        _sqlPassword = ctx.SqlCredentials?.Password ?? string.Empty;
        _pickSqlCertificate = pickSqlCertificate ?? PickSqlCertificateAsync;
        if (!string.IsNullOrWhiteSpace(ctx.SqlServerCertificateSourcePath))
        {
            try
            {
                var validation = SqlServerCertificateEnrollment.ValidateSource(
                    ctx.SqlServerCertificateSourcePath);
                _sqlCertificatePath = validation.SourcePath;
                _sqlCertificateStatus = "Public certificate validated and ready to pin.";
            }
            catch
            {
                _sqlCertificatePath = ctx.SqlServerCertificateSourcePath;
                _sqlCertificateSelectionInvalid = true;
                _sqlCertificateStatus =
                    "This certificate is invalid. Select one public .cer, .der, or single-certificate PEM file.";
            }
        }

        InstallCommand = new RelayCommand(Install, CanInstall);
        SelectSqlCertificateCommand = new RelayCommand(
            () => _ = SelectSqlCertificateAsync());
        ClearSqlCertificateCommand = new RelayCommand(
            ClearSqlCertificate,
            () => !string.IsNullOrWhiteSpace(_sqlCertificatePath));
    }

    public string InstallPath
    {
        get => _installPath;
        set { if (SetField(ref _installPath, value)) InstallCommand.RaiseCanExecuteChanged(); }
    }

    public bool IsInstallPathLocked => true;
    public string PageTitle => _ctx.ConfigureInstalledCohort
        ? "Workstation settings"
        : "Install destination";
    public string PageDetail => _ctx.ConfigureInstalledCohort
        ? "Confirm the detected PioneerRx connection. The signed MSI installation stays in place."
        : "Confirm the detected PioneerRx SQL connection. Installation remains blocked until this workstation can prove the pharmacy data path.";
    public string ActionLabel => _ctx.ConfigureInstalledCohort
        ? "Connect workstation"
        : "Install";
    public string ActionHint => _ctx.ConfigureInstalledCohort
        ? "Connect updates protected configuration only. Installed binaries and Windows service registrations are not replaced."
        : "Install starts when you click Install. It takes about 60 seconds.";

    public string SqlServer
    {
        get => _sqlServer;
        set
        {
            if (SetField(ref _sqlServer, value))
            {
                InstallCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(IsSqlMissing));
            }
        }
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

    public string SqlCertificatePath => _sqlCertificatePath;
    public string SqlCertificateStatus => _sqlCertificateStatus;
    public bool HasSqlCertificate => !string.IsNullOrWhiteSpace(_sqlCertificatePath);

    public RelayCommand InstallCommand { get; }
    public RelayCommand SelectSqlCertificateCommand { get; }
    public RelayCommand ClearSqlCertificateCommand { get; }

    /// <summary>Visible fail-closed explanation for a missing required SQL target.</summary>
    public bool IsSqlMissing => string.IsNullOrWhiteSpace(_sqlServer);

    private bool CanInstall()
    {
        if (string.IsNullOrWhiteSpace(_installPath)) return false;
        if (_sqlCertificateSelectionInvalid) return false;
        // Pharmacy activation and device-authority promotion both require an
        // exact live PioneerRx SQL/schema proof. A disconnected install cannot
        // honestly complete its probation health milestone.
        if (string.IsNullOrWhiteSpace(_sqlServer)) return false;
        if (string.IsNullOrWhiteSpace(_sqlDatabase)) return false;
        if (_useSqlAuth && (string.IsNullOrWhiteSpace(_sqlUser) || string.IsNullOrWhiteSpace(_sqlPassword)))
            return false;
        return true;
    }

    private void Install()
    {
        if (!string.Equals(
                Path.GetFullPath(_installPath.Trim()),
                Path.GetFullPath(UninstallOrchestrator.DefaultInstallDir),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "SuavoAgent is fixed to the Windows Installer-owned directory.");
        _ctx.InstallDir = UninstallOrchestrator.DefaultInstallDir;
        _ctx.SqlCredentials = new SqlCredentialDiscovery.SqlCredentials(
            Server: _sqlServer.Trim(),
            Database: _sqlDatabase.Trim(),
            User: _useSqlAuth ? _sqlUser.Trim() : null,
            Password: _useSqlAuth ? _sqlPassword : null);
        _ctx.SqlServerCertificateSourcePath = HasSqlCertificate
            ? _sqlCertificatePath
            : null;
        _onInstall();
    }

    internal async Task SelectSqlCertificateAsync()
    {
        string? selected;
        try
        {
            selected = await _pickSqlCertificate().ConfigureAwait(true);
        }
        catch
        {
            _sqlCertificateStatus =
                "The certificate picker could not open. Your current selection was not changed.";
            RaiseCertificateProperties();
            return;
        }
        if (string.IsNullOrWhiteSpace(selected)) return;
        try
        {
            _sqlCertificatePath = Path.GetFullPath(selected);
            var validation = SqlServerCertificateEnrollment.ValidateSource(
                _sqlCertificatePath);
            _sqlCertificatePath = validation.SourcePath;
            _sqlCertificateSelectionInvalid = false;
            _sqlCertificateStatus = "Public certificate validated and ready to pin.";
        }
        catch
        {
            _sqlCertificatePath = selected;
            _sqlCertificateSelectionInvalid = true;
            _sqlCertificateStatus =
                "This certificate is invalid. Select one public .cer, .der, or single-certificate PEM file.";
        }
        RaiseCertificateProperties();
    }

    private void ClearSqlCertificate()
    {
        _sqlCertificatePath = string.Empty;
        _sqlCertificateSelectionInvalid = false;
        _sqlCertificateStatus =
            "Optional — select the SQL Server public certificate only for a self-signed deployment.";
        RaiseCertificateProperties();
    }

    private void RaiseCertificateProperties()
    {
        RaisePropertyChanged(nameof(SqlCertificatePath));
        RaisePropertyChanged(nameof(SqlCertificateStatus));
        RaisePropertyChanged(nameof(HasSqlCertificate));
        InstallCommand.RaiseCanExecuteChanged();
        ClearSqlCertificateCommand.RaiseCanExecuteChanged();
    }

    private static async Task<string?> PickSqlCertificateAsync()
    {
        if (Application.Current?.ApplicationLifetime is not
                IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            return null;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select the PioneerRx SQL Server public certificate",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Public certificates")
                {
                    Patterns = ["*.cer", "*.der", "*.pem"],
                    MimeTypes = ["application/pkix-cert", "application/x-pem-file"],
                },
            ],
        });
        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }
}
