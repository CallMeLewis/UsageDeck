using System.ComponentModel;
using System.Runtime.CompilerServices;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App;

public sealed class FirstRunProviderOption : INotifyPropertyChanged
{
    private string _discoveryDetail = "UsageDeck is checking this PC.";
    private string _discoveryText = "Checking…";
    private bool _isSelected;

    public FirstRunProviderOption(ProviderSettingsPresentation provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        this.Id = provider.Id;
        this.DisplayName = provider.DisplayName;
        this.ProviderKey = provider.ProviderKey;
        this.UsageSource = provider.UsageSource;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProviderId Id { get; }

    public string DisplayName { get; }

    public string ProviderKey { get; }

    public string UsageSource { get; }

    public string DiscoveryText
    {
        get => this._discoveryText;
        private set => this.SetField(ref this._discoveryText, value);
    }

    public string DiscoveryDetail
    {
        get => this._discoveryDetail;
        private set
        {
            if (this.SetField(ref this._discoveryDetail, value))
            {
                this.OnPropertyChanged(nameof(this.AccessibleName));
            }
        }
    }

    public string AccessibleName =>
        $"{this.DisplayName}, {this.UsageSource}, {this.DiscoveryText}. {this.DiscoveryDetail}";

    public bool IsSelected
    {
        get => this._isSelected;
        set => this.SetField(ref this._isSelected, value);
    }

    public ProviderDiscoveryState? DiscoveryState { get; private set; }

    public void ApplyDiscovery(ProviderDiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ProviderId != this.Id)
        {
            throw new ArgumentException("The discovery result belongs to another provider.", nameof(result));
        }

        this.DiscoveryState = result.State;
        this.DiscoveryText = result.State switch
        {
            ProviderDiscoveryState.Detected => "Detected",
            ProviderDiscoveryState.NotDetected => "Not detected",
            _ => "Set up in Settings",
        };
        this.DiscoveryDetail = result.Detail;
        this.OnPropertyChanged(nameof(this.AccessibleName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        this.OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal static class FirstRunSettings
{
    public static AppSettings Create(
        AppSettings current,
        IEnumerable<ProviderId> selectedProviders,
        AppThemePreference theme,
        bool notificationsEnabled)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(selectedProviders);
        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(nameof(theme));
        }

        HashSet<ProviderId> selected = selectedProviders.ToHashSet();
        ProviderId[] enabled = ProviderId.Supported.Where(selected.Contains).ToArray();
        if (enabled.Length == 0)
        {
            throw new ArgumentException("Select at least one provider.", nameof(selectedProviders));
        }

        ProviderId defaultProvider = enabled.Length > 1 && current.IsAllTabEnabled
            ? ProviderId.All
            : enabled[0];
        return current with
        {
            EnabledProviders = enabled,
            DefaultProvider = defaultProvider,
            Theme = theme,
            AreNotificationsEnabled = notificationsEnabled,
        };
    }

    public static AppSettings CreateDefaults(AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return AppSettings.Default with { UpdateChannel = current.UpdateChannel };
    }
}
