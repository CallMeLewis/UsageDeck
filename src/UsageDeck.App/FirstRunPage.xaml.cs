using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App;

public sealed partial class FirstRunPage : Page, IDisposable
{
    private const int TotalSteps = 3;

    private readonly ProviderDiscoveryService _providerDiscovery;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _hasUserChangedProviderSelection;
    private bool _isApplyingProviderSelection;
    private bool _isApplyingTheme;
    private bool _isDisposed;
    private bool _isInitialising = true;
    private bool _isSaving;
    private AppThemePreference _selectedTheme;
    private int _step = 1;

    public FirstRunPage()
        : this(((App)Application.Current).ProviderDiscovery)
    {
    }

    internal FirstRunPage(ProviderDiscoveryService providerDiscovery)
    {
        this._providerDiscovery = providerDiscovery
            ?? throw new ArgumentNullException(nameof(providerDiscovery));
        this.ProviderOptions = new ObservableCollection<FirstRunProviderOption>(
            ProviderId.Supported.Select(providerId => new FirstRunProviderOption(
                ProviderSettingsPresentation.All[providerId])));

        this.InitializeComponent();
        App app = (App)Application.Current;
        this._selectedTheme = app.CurrentSettings.Theme;
        this.ImportantNotificationsToggle.IsOn = app.CurrentSettings.AreNotificationsEnabled;
        this.SelectTheme(app.CurrentSettings.Theme);
        this._isInitialising = false;
        this.RefreshNotificationStatus();
        this.UpdateStepPresentation();
        _ = this.DiscoverProvidersAsync();
    }

    public ObservableCollection<FirstRunProviderOption> ProviderOptions { get; }

    public void Dispose()
    {
        if (this._isDisposed)
        {
            return;
        }

        this._isDisposed = true;
        this._lifetimeCancellation.Cancel();
        this._lifetimeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task DiscoverProvidersAsync()
    {
        CancellationToken cancellationToken = this._lifetimeCancellation.Token;
        try
        {
            IReadOnlyList<ProviderDiscoveryResult> results = await Task.Run(
                () => this._providerDiscovery.Discover(cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            this._isApplyingProviderSelection = true;
            try
            {
                foreach (ProviderDiscoveryResult result in results)
                {
                    FirstRunProviderOption option = this.ProviderOptions.Single(value => value.Id == result.ProviderId);
                    option.ApplyDiscovery(result);
                }

                if (!this._hasUserChangedProviderSelection)
                {
                    foreach (FirstRunProviderOption option in this.ProviderOptions)
                    {
                        option.IsSelected = option.DiscoveryState == ProviderDiscoveryState.Detected;
                    }
                }
            }
            finally
            {
                this._isApplyingProviderSelection = false;
            }

            int detectedCount = results.Count(result => result.State == ProviderDiscoveryState.Detected);
            this.ProviderDetectionText.Text = detectedCount switch
            {
                0 => "No providers were detected. Choose any provider to set it up manually.",
                1 => "1 provider detected. You can change the selection.",
                _ => $"{detectedCount} providers detected. You can change the selection.",
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            this.ProviderDetectionText.Text = "UsageDeck could not finish checking this PC. Choose providers manually.";
        }
        finally
        {
            if (!this._isDisposed)
            {
                this.ProviderDetectionProgress.IsActive = false;
                this.ProviderDetectionProgress.Visibility = Visibility.Collapsed;
                this.UpdateProviderSelectionPresentation();
            }
        }
    }

    private void ProviderCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (this._isInitialising)
        {
            return;
        }

        if (sender is CheckBox { DataContext: FirstRunProviderOption option } checkBox)
        {
            option.IsSelected = checkBox.IsChecked == true;
        }

        if (!this._isApplyingProviderSelection)
        {
            this._hasUserChangedProviderSelection = true;
        }

        this.UpdateProviderSelectionPresentation();
    }

    private void UpdateProviderSelectionPresentation()
    {
        bool hasSelection = this.ProviderOptions.Any(option => option.IsSelected);
        this.ProviderSelectionHint.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        if (this._step == 1 && !this._isSaving)
        {
            this.ContinueButton.IsEnabled = hasSelection;
        }
    }

    private void ThemeRadioButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this._isInitialising
            || this._isApplyingTheme
            || this.ThemeRadioButtons.SelectedItem is not RadioButton selected
            || !Enum.TryParse(selected.Tag?.ToString(), ignoreCase: false, out AppThemePreference theme))
        {
            return;
        }

        this._selectedTheme = theme;
        ((App)Application.Current).PreviewFirstRunTheme(theme);
    }

    private void SelectTheme(AppThemePreference theme)
    {
        this._isApplyingTheme = true;
        try
        {
            this.ThemeRadioButtons.SelectedItem = this.ThemeRadioButtons.Items
                .OfType<RadioButton>()
                .First(item => string.Equals(
                    item.Tag?.ToString(),
                    theme.ToString(),
                    StringComparison.Ordinal));
        }
        finally
        {
            this._isApplyingTheme = false;
        }
    }

    private void ImportantNotificationsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (this._isInitialising)
        {
            return;
        }

        this.SendTestNotificationButton.IsEnabled = this.ImportantNotificationsToggle.IsOn
            && ((App)Application.Current).GetNotificationDeliveryStatus().CanSend;
    }

    private void SendTestNotificationButton_Click(object sender, RoutedEventArgs e)
    {
        NotificationDeliveryResult result = ((App)Application.Current).TryShowTestNotification();
        this.RefreshNotificationStatus();
        this.ShowMessage(
            result.WasDelivered
                ? "The test notification was sent to Windows."
                : result.FailureMessage ?? "Windows notification delivery is unavailable.",
            result.WasDelivered ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async void OpenWindowsNotificationSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool launched = await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:notifications"));
            if (!launched)
            {
                this.ShowMessage("Windows notification settings could not be opened.", InfoBarSeverity.Error);
            }
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException)
        {
            this.ShowMessage("Windows notification settings could not be opened.", InfoBarSeverity.Error);
        }
    }

    private void RefreshNotificationStatus()
    {
        NotificationDeliveryStatus status = ((App)Application.Current).GetNotificationDeliveryStatus();
        (string text, string glyph) = status.State switch
        {
            NotificationDeliveryState.Ready => ("Windows notifications are available", "\uE73E"),
            NotificationDeliveryState.Disabled => ("Notifications are blocked by Windows", "\uE711"),
            _ => ("Windows notification delivery is unavailable", "\uE946"),
        };
        this.NotificationStatusText.Text = text;
        this.NotificationStatusDetail.Text = status.Detail;
        this.NotificationStatusGlyph.Glyph = glyph;
        this.SendTestNotificationButton.IsEnabled = this.ImportantNotificationsToggle.IsOn && status.CanSend;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (this._step <= 1 || this._isSaving)
        {
            return;
        }

        this._step--;
        this.UpdateStepPresentation();
    }

    private async void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (this._isSaving)
        {
            return;
        }

        if (this._step < TotalSteps)
        {
            this._step++;
            this.UpdateStepPresentation();
            return;
        }

        App app = (App)Application.Current;
        AppSettings settings = FirstRunSettings.Create(
            app.CurrentSettings,
            this.ProviderOptions.Where(option => option.IsSelected).Select(option => option.Id),
            this._selectedTheme,
            this.ImportantNotificationsToggle.IsOn);
        await this.SaveAndFinishAsync(settings);
    }

    private async void UseDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        if (this._isSaving)
        {
            return;
        }

        App app = (App)Application.Current;
        await this.SaveAndFinishAsync(FirstRunSettings.CreateDefaults(app.CurrentSettings));
    }

    private async Task SaveAndFinishAsync(AppSettings settings)
    {
        CancellationToken cancellationToken = this._lifetimeCancellation.Token;
        this.SetSavingState(true);
        this.SetupInfoBar.IsOpen = false;
        try
        {
            await ((App)Application.Current).CompleteFirstRunAsync(settings, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            this.ShowMessage(
                "UsageDeck could not save setup. Check that your local app-data folder is available, then try again.",
                InfoBarSeverity.Error);
            this.SetSavingState(false);
        }
    }

    private void SetSavingState(bool saving)
    {
        this._isSaving = saving;
        this.BackButton.IsEnabled = !saving && this._step > 1;
        this.UseDefaultsButton.IsEnabled = !saving;
        this.ContinueButton.IsEnabled = !saving
            && (this._step != 1 || this.ProviderOptions.Any(option => option.IsSelected));
        this.ContinueButton.Content = saving ? "Saving…" : this._step == TotalSteps ? "Finish" : "Continue";
        this.SaveProgress.IsActive = saving;
        this.SaveProgress.Visibility = saving ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStepPresentation()
    {
        this.StepText.Text = $"Step {this._step} of {TotalSteps}";
        this.StepProgress.Value = this._step;
        this.ProvidersPanel.Visibility = this._step == 1 ? Visibility.Visible : Visibility.Collapsed;
        this.ThemePanel.Visibility = this._step == 2 ? Visibility.Visible : Visibility.Collapsed;
        this.NotificationsPanel.Visibility = this._step == 3 ? Visibility.Visible : Visibility.Collapsed;
        this.BackButton.IsEnabled = this._step > 1 && !this._isSaving;
        this.ContinueButton.Content = this._step == TotalSteps ? "Finish" : "Continue";
        this.ContinueButton.IsEnabled = !this._isSaving
            && (this._step != 1 || this.ProviderOptions.Any(option => option.IsSelected));
        this.SetupInfoBar.IsOpen = false;

        if (this._step == 2)
        {
            this.ThemeRadioButtons.Focus(FocusState.Programmatic);
        }
        else if (this._step == 3)
        {
            this.ImportantNotificationsToggle.Focus(FocusState.Programmatic);
            this.RefreshNotificationStatus();
        }

        AutomationProperties.SetHelpText(this.ContinueButton, this._step == TotalSteps
            ? "Save setup and open UsageDeck."
            : "Continue to the next setup step.");
    }

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        if (this._isDisposed)
        {
            return;
        }

        this.SetupInfoBar.Message = message;
        this.SetupInfoBar.Severity = severity;
        this.SetupInfoBar.IsOpen = true;
    }
}
