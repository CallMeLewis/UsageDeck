using System.Runtime.InteropServices;
using UsageDeck.Core.Providers;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace UsageDeck.App;

internal readonly record struct NotificationDeliveryResult(
    bool WasDelivered,
    string? FailureMessage)
{
    public static NotificationDeliveryResult Delivered { get; } = new(true, null);

    public static NotificationDeliveryResult Failed(string message) => new(false, message);
}

internal enum NotificationDeliveryState
{
    Ready,
    Disabled,
    Unavailable,
}

internal readonly record struct NotificationDeliveryStatus(
    NotificationDeliveryState State,
    string Detail)
{
    public bool CanSend => this.State == NotificationDeliveryState.Ready;
}

internal sealed class WindowsNotificationService : IDisposable
{
    internal const string DisplayName = "UsageDeck";

    private readonly Func<AppNotificationManager> _managerFactory;
    private readonly Func<bool> _supportProbe;
    private bool _isDisposed;
    private bool _isRegistered;
    private AppNotificationManager? _manager;
    private string? _unavailableReason;

    public WindowsNotificationService()
        : this(AppNotificationManager.IsSupported, () => AppNotificationManager.Default)
    {
    }

    internal WindowsNotificationService(
        Func<bool> supportProbe,
        Func<AppNotificationManager> managerFactory)
    {
        this._supportProbe = supportProbe ?? throw new ArgumentNullException(nameof(supportProbe));
        this._managerFactory = managerFactory ?? throw new ArgumentNullException(nameof(managerFactory));
    }

    public event Action<ProviderId?>? Activated;

    public bool Initialise()
    {
        AppNotificationManager? manager = null;
        try
        {
            if (!this._supportProbe())
            {
                this._unavailableReason =
                    "Windows does not expose app notifications to this build. "
                    + "Self-contained Windows App SDK deployments require the separate Singleton package.";
                return false;
            }

            manager = this._managerFactory();
            // Unpackaged apps must subscribe before registration so activation stays in this process.
            manager.NotificationInvoked += this.Manager_NotificationInvoked;
            manager.Register(DisplayName, CreateIconUri());
            this._manager = manager;
            this._isRegistered = true;
            this._unavailableReason = null;
            return true;
        }
        catch (Exception exception) when (exception is COMException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            if (manager is not null)
            {
                manager.NotificationInvoked -= this.Manager_NotificationInvoked;
            }

            this._unavailableReason = DescribeRegistrationFailure(exception);
            System.Diagnostics.Debug.WriteLine(exception);
            return false;
        }
    }

    public NotificationDeliveryResult Show(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        AppNotificationManager? manager = this._manager;
        if (!this._isRegistered || this._isDisposed || manager is null)
        {
            return NotificationDeliveryResult.Failed(
                this._unavailableReason ?? "Windows notification registration is unavailable.");
        }

        AppNotificationBuilder builder = new AppNotificationBuilder()
            .AddArgument("provider", message.ProviderId.Value)
            .AddText(message.Title)
            .AddText(message.Body);
        if (message.ActionUri is not null && message.ActionLabel is not null)
        {
            builder.AddButton(new AppNotificationButton(message.ActionLabel)
                .SetInvokeUri(message.ActionUri));
        }

        try
        {
            AppNotificationSetting setting = manager.Setting;
            if (setting != AppNotificationSetting.Enabled)
            {
                return NotificationDeliveryResult.Failed(DescribeSetting(setting));
            }

            manager.Show(builder.BuildNotification());
            return NotificationDeliveryResult.Delivered;
        }
        catch (Exception exception) when (exception is COMException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            // Notification delivery must never interrupt usage refreshes.
            System.Diagnostics.Debug.WriteLine(exception);
            return NotificationDeliveryResult.Failed(
                $"Windows rejected the notification ({FormatHResult(exception)}).");
        }
    }

    public NotificationDeliveryStatus GetStatus()
    {
        AppNotificationManager? manager = this._manager;
        if (!this._isRegistered || this._isDisposed || manager is null)
        {
            return new NotificationDeliveryStatus(
                NotificationDeliveryState.Unavailable,
                this._unavailableReason ?? "Windows notification registration is unavailable.");
        }

        try
        {
            return CreateStatus(manager.Setting);
        }
        catch (Exception exception) when (exception is COMException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            return new NotificationDeliveryStatus(
                NotificationDeliveryState.Unavailable,
                $"Windows notification status could not be read ({FormatHResult(exception)}).");
        }
    }

    internal static NotificationDeliveryStatus CreateStatus(AppNotificationSetting setting) => setting switch
    {
        AppNotificationSetting.Enabled => new NotificationDeliveryStatus(
            NotificationDeliveryState.Ready,
            "Windows can show notifications from UsageDeck."),
        AppNotificationSetting.Unsupported => new NotificationDeliveryStatus(
            NotificationDeliveryState.Unavailable,
            DescribeSetting(setting)),
        AppNotificationSetting.DisabledForApplication
            or AppNotificationSetting.DisabledForUser
            or AppNotificationSetting.DisabledByGroupPolicy
            or AppNotificationSetting.DisabledByManifest => new NotificationDeliveryStatus(
            NotificationDeliveryState.Disabled,
            DescribeSetting(setting)),
        _ => new NotificationDeliveryStatus(
            NotificationDeliveryState.Unavailable,
            DescribeSetting(setting)),
    };

    internal static string DescribeSetting(AppNotificationSetting setting) => setting switch
    {
        AppNotificationSetting.DisabledForApplication =>
            "Notifications are turned off for UsageDeck in Windows Settings.",
        AppNotificationSetting.DisabledForUser =>
            "Windows notifications are turned off for this user account.",
        AppNotificationSetting.DisabledByGroupPolicy =>
            "Notifications are disabled by Windows group policy.",
        AppNotificationSetting.DisabledByManifest =>
            "This app's manifest disables notifications.",
        AppNotificationSetting.Unsupported =>
            "Windows does not support app notifications for this build.",
        _ => "Windows notification delivery is unavailable.",
    };

    internal static Uri CreateIconUri() => new(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png")));

    public void Dispose()
    {
        if (this._isDisposed)
        {
            return;
        }

        this._isDisposed = true;
        AppNotificationManager? manager = this._manager;
        if (this._isRegistered && manager is not null)
        {
            manager.NotificationInvoked -= this.Manager_NotificationInvoked;
            try
            {
                manager.Unregister();
            }
            catch (Exception exception) when (exception is COMException
                or InvalidOperationException
                or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(exception);
            }

            this._isRegistered = false;
        }

        this._manager = null;

        GC.SuppressFinalize(this);
    }

    private void Manager_NotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        ProviderId? providerId = args.Arguments.TryGetValue("provider", out string? providerValue)
            && !string.IsNullOrWhiteSpace(providerValue)
                ? new ProviderId(providerValue)
                : null;
        this.Activated?.Invoke(providerId);
    }

    private static string DescribeRegistrationFailure(Exception exception) =>
        $"Windows notification registration failed ({FormatHResult(exception)}).";

    private static string FormatHResult(Exception exception) => $"0x{exception.HResult:X8}";
}
