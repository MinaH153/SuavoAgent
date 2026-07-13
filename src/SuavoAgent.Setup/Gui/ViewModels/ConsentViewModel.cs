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
    private string _complianceMode = "hipaa";

    public ConsentViewModel(InstallContext ctx, Action onAgreed)
    {
        _ctx = ctx;
        _onAgreed = onAgreed;
        AgreeCommand = new RelayCommand(Agree, CanAgree);
    }

    /// <summary>
    /// Compliance mode from the verified verticalConfig ("hipaa", "none", …).
    /// Defaults to "hipaa" (fail-closed). Set by InstallOrchestrator before
    /// showing the Consent step.
    /// </summary>
    public string ComplianceMode
    {
        get => _complianceMode;
        set
        {
            if (SetField(ref _complianceMode, value ?? "hipaa"))
            {
                RaisePropertyChanged(nameof(ShowHipaaDisclosure));
                RaisePropertyChanged(nameof(RequiresEmployeeNotice));
                RaisePropertyChanged(nameof(MissingHint));
                AgreeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Show the full HIPAA disclosure card only in HIPAA mode.</summary>
    public bool ShowHipaaDisclosure =>
        string.Equals(_complianceMode, "hipaa", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>The 2-letter state code typed into the State field; bound directly.
    /// Normalized to upper-case so the notice logic and the saved receipt see a
    /// clean code regardless of how it was typed.</summary>
    public string StateCode
    {
        get => _state;
        set
        {
            var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (SetField(ref _state, normalized))
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

    /// <summary>
    /// Every HIPAA deployment requires a completed employee notice before
    /// observation can be authorized. State classification still drives the
    /// stronger statutory banner and receipt metadata, but it cannot weaken
    /// the product-wide transparency gate.
    /// </summary>
    public bool RequiresEmployeeNotice => ShowHipaaDisclosure;

    public string NoticeBannerText
    {
        get
        {
            var up = (_state ?? string.Empty).Trim().ToUpperInvariant();
            if (ConsentReceiptData.RequiresMandatoryNotice(up))
                return $"{up} requires written employee notice before monitoring. Confirm distribution.";
            if (ConsentReceiptData.IsHighRisk(up))
                return $"{up} has strong privacy protections. Distribute the completed notice before monitoring.";
            return "Suavo requires a completed employee notice before HIPAA observation.";
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
            Timestamp: DateTimeOffset.UtcNow,
            ComplianceMode: _complianceMode);

        _onAgreed();
    }
}
