using System.Diagnostics;
using System.Windows.Input;
using SuavoAgent.Setup.Gui.Services;

namespace SuavoAgent.Setup.Gui.ViewModels;

internal sealed class SuccessViewModel
{
    public SuccessViewModel(InstallContext ctx, Action onFinish)
    {
        InstallPath = ctx.InstallDir;
        DataPath = ctx.DataDir;
        AgentId = ctx.AgentId ?? "unknown";
        SqlSummary = ctx.SqlCredentials is { } c
            ? $"{c.Server} / {c.Database} ({(c.IsWindowsAuth ? "Windows auth" : $"SQL: {c.User}")})"
            : "unknown";
        DashboardUrl = ctx.Config.CloudUrl.TrimEnd('/') + "/dashboard";

        OpenDashboardCommand = new RelayCommand(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = DashboardUrl,
                    UseShellExecute = true,
                });
            }
            catch { /* best effort — browser may be unavailable */ }
        });

        FinishCommand = new RelayCommand(onFinish);
    }

    public string InstallPath { get; }
    public string DataPath { get; }
    public string AgentId { get; }
    public string SqlSummary { get; }
    public string DashboardUrl { get; }

    public ICommand OpenDashboardCommand { get; }
    public ICommand FinishCommand { get; }
}
