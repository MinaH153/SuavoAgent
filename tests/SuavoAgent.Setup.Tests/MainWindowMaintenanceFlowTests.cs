using System.Net;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using SuavoAgent.Setup.Gui.Services;
using SuavoAgent.Setup.Gui.ViewModels;
using SuavoAgent.Setup.Gui.Views;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class MainWindowMaintenanceFlowTests
{
    [Fact]
    public void Constructor_RejectsAmbiguousMaintenanceEntryMode()
    {
        Assert.Throws<ArgumentException>(() =>
            new MainWindowViewModel(_ => { }, startInUninstall: true, startInRepair: true));
        Assert.Throws<ArgumentException>(() =>
            new MainWindowViewModel(
                _ => { },
                startInRepair: true,
                configureInstalledCohort: true));
    }

    [AvaloniaFact]
    public void DedicatedUninstallEntry_CancelClosesSuccessfullyWithoutPairing()
    {
        var exitCode = -1;
        var vm = new MainWindowViewModel(
            code => exitCode = code,
            startInUninstall: true);
        var confirm = Assert.IsType<UninstallConfirmView>(vm.CurrentView);
        var confirmVm = Assert.IsType<UninstallConfirmViewModel>(confirm.DataContext);

        confirmVm.CancelCommand.Execute(null);

        Assert.Equal(0, exitCode);
        Assert.Equal("Uninstall · Confirm", vm.StepLabel);
    }

    [AvaloniaFact]
    public void DedicatedRepairEntry_CancelClosesSuccessfullyWithoutPairing()
    {
        var exitCode = -1;
        var vm = new MainWindowViewModel(
            code => exitCode = code,
            startInRepair: true);
        var confirm = Assert.IsType<RepairConfirmView>(vm.CurrentView);
        var confirmVm = Assert.IsType<RepairConfirmViewModel>(confirm.DataContext);

        confirmVm.CancelCommand.Execute(null);

        Assert.Equal(0, exitCode);
        Assert.Equal("Repair services · Confirm", vm.StepLabel);
    }

    [AvaloniaFact]
    public void WelcomeUninstall_CancelReturnsToWelcomeInsteadOfExiting()
    {
        var vm = new MainWindowViewModel();
        var welcome = Invoke<UserControl>(vm, "BuildWelcome");
        SetCurrentView(vm, welcome);

        var welcomeVm = Assert.IsType<WelcomeViewModel>(welcome.DataContext);
        welcomeVm.UninstallCommand.Execute(null);
        var confirm = Assert.IsType<UninstallConfirmView>(vm.CurrentView);
        Assert.Equal("Uninstall · Confirm", vm.StepLabel);

        Assert.IsType<UninstallConfirmViewModel>(confirm.DataContext)
            .CancelCommand.Execute(null);
        Assert.IsType<WelcomeView>(vm.CurrentView);
        Assert.Equal("Welcome", vm.StepLabel);
    }

    [AvaloniaFact]
    public void ContextBoundTransitions_RenderConsentDestinationAndSuccess()
    {
        var exitCode = -1;
        var vm = new MainWindowViewModel();
        var context = Context();
        SetContext(vm, context);

        Invoke(vm, "GoToConsent");
        Assert.IsType<ConsentViewModel>(
            Assert.IsType<ConsentView>(vm.CurrentView).DataContext);
        Assert.Equal("Step 2 of 5 · Terms & consent", vm.StepLabel);

        Invoke(vm, "GoToDestination");
        Assert.IsType<DestinationViewModel>(
            Assert.IsType<DestinationView>(vm.CurrentView).DataContext);
        Assert.Equal("Step 3 of 5 · Install destination", vm.StepLabel);

        Invoke(vm, "GoToSuccess");
        var success = Assert.IsType<SuccessViewModel>(
            Assert.IsType<SuccessView>(vm.CurrentView).DataContext);
        Assert.Equal("Done", vm.StepLabel);
        Assert.Equal("https://suavollc.com/pharmacy/agent", success.DashboardUrl);
        success.FinishCommand.Execute(null);
        Assert.Equal(-1, exitCode); // parameterless design constructor has no shutdown callback
    }

    [AvaloniaFact]
    public void NullContextTransitions_AreNoOps()
    {
        var vm = new MainWindowViewModel();
        var initial = vm.CurrentView;

        Invoke(vm, "GoToSystemCheck");
        Invoke(vm, "GoToConsent");
        Invoke(vm, "GoToDestination");
        Invoke(vm, "GoToSuccess");

        Assert.Same(initial, vm.CurrentView);
        Assert.Equal("Welcome", vm.StepLabel);
    }

    [AvaloniaFact]
    public void MaintenanceSuccessAndErrorScreens_WireTerminalActions()
    {
        var exits = new List<int>();
        var vm = new MainWindowViewModel(exits.Add, startInRepair: true);

        Invoke(vm, "GoToRepairSuccess");
        var repaired = Assert.IsType<UninstallSuccessViewModel>(
            Assert.IsType<UninstallSuccessView>(vm.CurrentView).DataContext);
        Assert.Equal("SuavoAgent services repaired", repaired.Headline);
        Assert.Contains("does not replace missing or modified", repaired.Message);
        repaired.FinishCommand.Execute(null);
        Assert.Equal([0], exits);

        Invoke(vm, "GoToUninstallSuccess");
        var removed = Assert.IsType<UninstallSuccessViewModel>(
            Assert.IsType<UninstallSuccessView>(vm.CurrentView).DataContext);
        Assert.Equal("SuavoAgent removed", removed.Headline);
        removed.FinishCommand.Execute(null);
        Assert.Equal([0, 0], exits);

        var retried = false;
        Invoke(vm, "GoToError", "Safe failure", "No sensitive detail", (Action)(() => retried = true));
        var error = Assert.IsType<ErrorViewModel>(
            Assert.IsType<ErrorView>(vm.CurrentView).DataContext);
        Assert.True(error.CanRetry);
        error.RetryCommand.Execute(null);
        error.CloseCommand.Execute(null);
        Assert.True(retried);
        Assert.Equal([0, 0, 1], exits);
    }

    [AvaloniaFact]
    public void PairingCancellationError_HasNoRetryAndFailsClosedOnExit()
    {
        var exits = new List<int>();
        var vm = new MainWindowViewModel(exits.Add, startInRepair: true);

        var view = Invoke<UserControl>(vm, "BuildNoConfigError");
        var error = Assert.IsType<ErrorViewModel>(view.DataContext);

        Assert.False(error.CanRetry);
        Assert.False(error.RetryCommand.CanExecute(null));
        Assert.Contains("https://suavollc.com", error.Detail, StringComparison.Ordinal);
        error.CloseCommand.Execute(null);
        Assert.Equal([1], exits);
    }

    [Theory]
    [InlineData("install", typeof(HttpRequestException), "SETUP-NETWORK")]
    [InlineData("install", typeof(UnauthorizedAccessException), "SETUP-ACCESS")]
    [InlineData("install", typeof(IOException), "SETUP-FILE-IO")]
    [InlineData("repair", typeof(InvalidOperationException), "SETUP-REPAIR-SAFE-FAIL")]
    [InlineData("uninstall", typeof(InvalidOperationException), "SETUP-UNINSTALL-SAFE-FAIL")]
    public void SafeFailureDetail_ClassifiesWithoutReflectingException(
        string operation,
        Type exceptionType,
        string supportCode)
    {
        var exception = (Exception)Activator.CreateInstance(
            exceptionType,
            "Jane Doe RX-123 secret")!;

        var detail = MainWindowViewModel.BuildSafeFailureDetail(operation, exception);

        Assert.Contains(supportCode, detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Jane Doe", detail, StringComparison.Ordinal);
        Assert.Contains(SetupLog.LogPath, detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SafeFailureDetail_RejectsMissingException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MainWindowViewModel.BuildSafeFailureDetail("install", null!));
    }

    [Theory]
    [InlineData(NativeRepairInstaller.UnsupportedHost, "installed, signed")]
    [InlineData(NativeRepairInstaller.InvalidCohort, "authentic")]
    [InlineData(NativeRepairInstaller.AuthorityRecoveryPending, "recovering")]
    [InlineData(NativeRepairInstaller.AclRepairFailed, "permissions")]
    [InlineData(NativeRepairInstaller.ServiceStopFailed, "pause")]
    [InlineData(NativeRepairInstaller.ServiceConfigFailed, "healthy state")]
    [InlineData(NativeRepairInstaller.ServiceStartFailed, "healthy state")]
    [InlineData(NativeRepairInstaller.CohortUnhealthy, "healthy state")]
    [InlineData(NativeRepairInstaller.LifecycleRegistrationFailed, "Windows Settings")]
    [InlineData(NativeRepairInstaller.LegacyLifecycleMigrationFailed, "healthy repair")]
    [InlineData(999, "healthy repair")]
    public void RepairFailureDetail_MapsEveryExitClassWithoutInternalDetails(
        int exitCode,
        string expectedGuidance)
    {
        var detail = MainWindowViewModel.BuildRepairFailureDetail(exitCode);

        Assert.Contains(expectedGuidance, detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"SETUP-REPAIR-{exitCode}", detail, StringComparison.Ordinal);
        Assert.Contains(SetupLog.LogPath, detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, true, true, "2 Windows service(s)")]
    [InlineData(0, false, true, "compliance evidence")]
    [InlineData(0, true, false, "file may still be locked")]
    [InlineData(0, true, true, "Some items could not be removed")]
    public void ResidueSummary_ReportsEveryObservableFailure(
        int servicesRemaining,
        bool dataDirRemoved,
        bool installDirRemoved,
        string expected)
    {
        var result = new ServiceInstaller.UninstallResult
        {
            ServicesRemaining = servicesRemaining,
            DataDirRemoved = dataDirRemoved,
            InstallDirRemoved = installDirRemoved,
        };
        var detail = InvokeStatic<string>("BuildResidueSummary", result);

        Assert.Contains(expected, detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("run the uninstaller again", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalUninstallWithoutSignedAuthorityReportsPendingAndUnchanged()
    {
        var detail = MainWindowViewModel.BuildUninstallPendingDetail(
            SelfUninstallFinalizationResult.Pending(
                "signed_cloud_authority_required"));

        Assert.Contains("Nothing on this PC was changed", detail, StringComparison.Ordinal);
        Assert.Contains("SETUP-UNINSTALL-AUTHORITY-REQUIRED", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("removed", detail, StringComparison.OrdinalIgnoreCase);
    }

    private static InstallContext Context() => new(new SetupConfig(
        PharmacyId: "11111111-1111-4111-8111-111111111111",
        ApiKey: "sagent_test_key",
        CloudUrl: "https://suavollc.com/",
        ReleaseTag: "v3.80.0",
        LearningMode: false,
        AgentId: "22222222-2222-4222-8222-222222222222"));

    private static void SetContext(MainWindowViewModel vm, InstallContext context)
    {
        var field = typeof(MainWindowViewModel).GetField(
            "_ctx",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(vm, context);
    }

    private static void SetCurrentView(MainWindowViewModel vm, UserControl view)
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.CurrentView),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        property.SetValue(vm, view);
    }

    private static void Invoke(MainWindowViewModel vm, string method, params object?[]? args) =>
        _ = Invoke<object?>(vm, method, args);

    private static T Invoke<T>(MainWindowViewModel vm, string method, params object?[]? args)
    {
        var target = typeof(MainWindowViewModel).GetMethod(
            method,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(target);
        return (T)target.Invoke(vm, args)!;
    }

    private static T InvokeStatic<T>(string method, params object?[]? args)
    {
        var target = typeof(MainWindowViewModel).GetMethod(
            method,
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(target);
        return (T)target.Invoke(null, args)!;
    }
}
