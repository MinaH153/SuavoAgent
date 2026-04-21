using SuavoAgent.Setup.Gui.Services;

namespace SuavoAgent.Setup.Gui.ViewModels;

internal sealed class ConsentViewModel : ViewModelBase
{
    private readonly InstallContext _ctx;
    private readonly Action _onAgreed;

    private string _name = string.Empty;
    private string _title = string.Empty;
    private string _state = string.Empty;
    private bool _agreedToTerms;
    private bool _agreedToNotice;

    public ConsentViewModel(InstallContext ctx, Action onAgreed)
    {
        _ctx = ctx;
        _onAgreed = onAgreed;
        AgreeCommand = new RelayCommand(Agree, CanAgree);
    }

    public string Name
    {
        get => _name;
        set { if (SetField(ref _name, value)) AgreeCommand.RaiseCanExecuteChanged(); }
    }

    public string Title
    {
        get => _title;
        set { if (SetField(ref _title, value)) AgreeCommand.RaiseCanExecuteChanged(); }
    }

    public string StateCode
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                AgreeCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(RequiresEmployeeNotice));
                RaisePropertyChanged(nameof(NoticeBannerText));
            }
        }
    }

    public bool AgreedToTerms
    {
        get => _agreedToTerms;
        set { if (SetField(ref _agreedToTerms, value)) AgreeCommand.RaiseCanExecuteChanged(); }
    }

    public bool AgreedToNotice
    {
        get => _agreedToNotice;
        set { if (SetField(ref _agreedToNotice, value)) AgreeCommand.RaiseCanExecuteChanged(); }
    }

    public bool RequiresEmployeeNotice => ConsentReceiptData.RequiresMandatoryNotice(_state ?? string.Empty);

    public string NoticeBannerText
    {
        get
        {
            var up = (_state ?? string.Empty).Trim().ToUpperInvariant();
            if (ConsentReceiptData.RequiresMandatoryNotice(up))
                return $"{up} requires written employee notice before monitoring. Confirm distribution.";
            if (ConsentReceiptData.IsHighRisk(up))
                return $"{up} has strong privacy protections. Employee notice strongly recommended.";
            return string.Empty;
        }
    }

    public RelayCommand AgreeCommand { get; }

    private bool CanAgree()
    {
        if (string.IsNullOrWhiteSpace(_name)) return false;
        if (string.IsNullOrWhiteSpace(_state)) return false;
        if (!_agreedToTerms) return false;
        if (RequiresEmployeeNotice && !_agreedToNotice) return false;
        return true;
    }

    private void Agree()
    {
        var state = _state.Trim().ToUpperInvariant();
        var titleFallback = string.IsNullOrWhiteSpace(_title) ? "Authorized Representative" : _title.Trim();

        _ctx.Consent = new ConsentReceiptData(
            AuthorizingName: _name.Trim(),
            AuthorizingTitle: titleFallback,
            BusinessState: state,
            MandatoryNoticeState: ConsentReceiptData.RequiresMandatoryNotice(state),
            EmployeeNoticeAcknowledged: _agreedToNotice || !RequiresEmployeeNotice,
            Timestamp: DateTimeOffset.UtcNow);

        _onAgreed();
    }
}
