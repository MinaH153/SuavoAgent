using SuavoAgent.Setup.Gui.Services;

namespace SuavoAgent.Setup.Gui.ViewModels;

/// <summary>A US state/territory the authorizing party picks from (no more blind 2-char code).</summary>
public sealed record UsState(string Code, string Name)
{
    public string Display => $"{Name} ({Code})";
    public override string ToString() => Display;
}

internal sealed class ConsentViewModel : ViewModelBase
{
    private readonly InstallContext _ctx;
    private readonly Action _onAgreed;

    private string _name = string.Empty;
    private string _title = string.Empty;
    private string _state = string.Empty;
    private UsState? _selectedState;
    private bool _agreedToTerms;
    private bool _agreedToNotice;

    /// <summary>50 states + DC, alphabetical — drives the state ComboBox.</summary>
    public static readonly IReadOnlyList<UsState> AllStates = new[]
    {
        new UsState("AL", "Alabama"), new UsState("AK", "Alaska"), new UsState("AZ", "Arizona"),
        new UsState("AR", "Arkansas"), new UsState("CA", "California"), new UsState("CO", "Colorado"),
        new UsState("CT", "Connecticut"), new UsState("DE", "Delaware"), new UsState("DC", "District of Columbia"),
        new UsState("FL", "Florida"), new UsState("GA", "Georgia"), new UsState("HI", "Hawaii"),
        new UsState("ID", "Idaho"), new UsState("IL", "Illinois"), new UsState("IN", "Indiana"),
        new UsState("IA", "Iowa"), new UsState("KS", "Kansas"), new UsState("KY", "Kentucky"),
        new UsState("LA", "Louisiana"), new UsState("ME", "Maine"), new UsState("MD", "Maryland"),
        new UsState("MA", "Massachusetts"), new UsState("MI", "Michigan"), new UsState("MN", "Minnesota"),
        new UsState("MS", "Mississippi"), new UsState("MO", "Missouri"), new UsState("MT", "Montana"),
        new UsState("NE", "Nebraska"), new UsState("NV", "Nevada"), new UsState("NH", "New Hampshire"),
        new UsState("NJ", "New Jersey"), new UsState("NM", "New Mexico"), new UsState("NY", "New York"),
        new UsState("NC", "North Carolina"), new UsState("ND", "North Dakota"), new UsState("OH", "Ohio"),
        new UsState("OK", "Oklahoma"), new UsState("OR", "Oregon"), new UsState("PA", "Pennsylvania"),
        new UsState("RI", "Rhode Island"), new UsState("SC", "South Carolina"), new UsState("SD", "South Dakota"),
        new UsState("TN", "Tennessee"), new UsState("TX", "Texas"), new UsState("UT", "Utah"),
        new UsState("VT", "Vermont"), new UsState("VA", "Virginia"), new UsState("WA", "Washington"),
        new UsState("WV", "West Virginia"), new UsState("WI", "Wisconsin"), new UsState("WY", "Wyoming"),
    };

    public IReadOnlyList<UsState> States => AllStates;

    public ConsentViewModel(InstallContext ctx, Action onAgreed)
    {
        _ctx = ctx;
        _onAgreed = onAgreed;
        AgreeCommand = new RelayCommand(Agree, CanAgree);
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetField(ref _name, value))
            {
                AgreeCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(MissingHint));
            }
        }
    }

    public string Title
    {
        get => _title;
        set { if (SetField(ref _title, value)) AgreeCommand.RaiseCanExecuteChanged(); }
    }

    /// <summary>ComboBox selection — the canonical way to set the state.</summary>
    public UsState? SelectedState
    {
        get => _selectedState;
        set
        {
            if (SetField(ref _selectedState, value))
                StateCode = value?.Code ?? string.Empty;
        }
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
                RaisePropertyChanged(nameof(MissingHint));
            }
        }
    }

    public bool AgreedToTerms
    {
        get => _agreedToTerms;
        set
        {
            if (SetField(ref _agreedToTerms, value))
            {
                AgreeCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(MissingHint));
            }
        }
    }

    public bool AgreedToNotice
    {
        get => _agreedToNotice;
        set
        {
            if (SetField(ref _agreedToNotice, value))
            {
                AgreeCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(MissingHint));
            }
        }
    }

    /// <summary>
    /// Live helper next to the always-visible CTA: says exactly what's still
    /// missing, and goes empty the moment the form is complete. Never a hidden
    /// button, never a mystery.
    /// </summary>
    public string MissingHint
    {
        get
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(_name)) missing.Add("your full name");
            if (string.IsNullOrWhiteSpace(_state)) missing.Add("your state");
            if (!_agreedToTerms) missing.Add("the authorization checkbox");
            if (RequiresEmployeeNotice && !_agreedToNotice) missing.Add("the employee-notice confirmation");
            return missing.Count == 0 ? string.Empty : "Still needed: " + string.Join(", ", missing) + ".";
        }
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
