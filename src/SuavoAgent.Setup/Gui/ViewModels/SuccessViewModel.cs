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
        BrainSummary = ctx.BrainInstalled
            ? $"{ctx.Config.Reasoning?.ModelId ?? "qwen3"} · installed and ready"
            : ctx.Config.Reasoning is { IsProvisionable: true }
                ? "finishing up in the background"
                : "configures automatically once available";
        // Land the operator on the pharmacy-facing SuavoAgent surface — NOT /dashboard,
        // which is the admin/platform Command Center a pharmacy user can't access ("no admin
        // access on this account"). The person who just installed is a pharmacy user, so the
        // post-install CTA must open the surface their role can actually see.
        DashboardUrl = ctx.Config.CloudUrl.TrimEnd('/') + "/pharmacy/agent";

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
    public string BrainSummary { get; }
    public string DashboardUrl { get; }

    public ICommand OpenDashboardCommand { get; }
    public ICommand FinishCommand { get; }
}
