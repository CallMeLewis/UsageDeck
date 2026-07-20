using System.Runtime.InteropServices;
using System.Windows.Input;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace UsageDeck.App;

public sealed partial class MainWindow : Window
{
    private const double MainMinimumHeight = 520;
    private const double MainMinimumWidth = 400;
    private const double SetupMinimumHeight = 560;
    private const double SetupMinimumWidth = 520;

    private bool _isFirstRun;
    private bool _isExiting;
    private double _minimumHeight;
    private double _minimumWidth;
    private AppThemePreference? _previewTheme;
    private ThemeSettings? _themeSettings;

    public MainWindow()
        : this(isFirstRun: false)
    {
    }

    internal MainWindow(bool isFirstRun)
    {
        this._isFirstRun = isFirstRun;
        this._minimumWidth = isFirstRun ? SetupMinimumWidth : MainMinimumWidth;
        this._minimumHeight = isFirstRun ? SetupMinimumHeight : MainMinimumHeight;
        this.ToggleWindowCommand = new RelayCommand(this.ToggleWindow);
        this.ShowWindowCommand = new RelayCommand(this.ShowWindow);
        this.RefreshCommand = new AsyncRelayCommand(this.RefreshAllAsync);
        this.SettingsCommand = new RelayCommand(this.ShowSettings);
        this.ExitCommand = new RelayCommand(this.ExitApplication);
        this.InitializeComponent();
        App app = (App)Application.Current;
        this.InitialiseThemeSettings(app);
        App.ApplyWindowAppearance(this, this.RootLayout, this.SolidBackground, app.CurrentSettings);
        app.SettingsChanged += this.App_SettingsChanged;

        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(this.WindowDragRegion);
        this.RootLayout.ActualThemeChanged += this.RootLayout_ActualThemeChanged;
        App.ApplyCaptionButtonColours(this, this.RootLayout);

        if (this.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        this.AppWindow.SetIcon("Assets/AppIcon.ico");
        WindowSizing.Configure(
            this,
            isFirstRun ? 640 : 500,
            isFirstRun ? 700 : 780,
            this._minimumWidth,
            this._minimumHeight);
        this.AppWindow.Changed += this.AppWindow_Changed;
        this.AppWindow.Closing += this.AppWindow_Closing;
    }

    public ICommand ToggleWindowCommand { get; }

    public ICommand ShowWindowCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand SettingsCommand { get; }

    public ICommand ExitCommand { get; }

    internal void PrepareForShutdown()
    {
        if (this._isExiting)
        {
            return;
        }

        this._isExiting = true;
        if (this.RootFrame.Content is FirstRunPage firstRunPage)
        {
            firstRunPage.Dispose();
        }

        if (this._themeSettings is not null)
        {
            this._themeSettings.Changed -= this.ThemeSettings_Changed;
        }

        this.TrayIcon.Dispose();
    }

    internal void RefreshAppearance(AppSettings settings)
    {
        AppSettings appearanceSettings = this._isFirstRun && this._previewTheme is AppThemePreference previewTheme
            ? settings with { Theme = previewTheme }
            : settings;
        App.ApplyWindowAppearance(this, this.RootLayout, this.SolidBackground, appearanceSettings);
        App.ApplyCaptionButtonColours(this, this.RootLayout);
    }

    internal void PreviewFirstRunTheme(AppThemePreference theme)
    {
        if (!this._isFirstRun)
        {
            return;
        }

        this._previewTheme = theme;
        this.RefreshAppearance(((App)Application.Current).CurrentSettings);
    }

    internal void ShowFirstRunPage()
    {
        if (this.RootFrame.Content is FirstRunPage)
        {
            return;
        }

        this._isFirstRun = true;
        this._previewTheme = ((App)Application.Current).CurrentSettings.Theme;
        this.SetMinimumSize(SetupMinimumWidth, SetupMinimumHeight);
        if (!this.RootFrame.Navigate(
            typeof(FirstRunPage),
            null,
            new SuppressNavigationTransitionInfo()))
        {
            throw new InvalidOperationException("The first-run page could not be opened.");
        }
    }

    internal void ShowMainPage(ProviderId? providerId = null)
    {
        FirstRunPage? firstRunPage = this.RootFrame.Content as FirstRunPage;
        this._isFirstRun = false;
        this._previewTheme = null;
        this.SetMinimumSize(MainMinimumWidth, MainMinimumHeight);
        this.RefreshAppearance(((App)Application.Current).CurrentSettings);

        if (this.RootFrame.Content is not MainPage)
        {
            NavigationTransitionInfo transition = firstRunPage is null
                ? new SuppressNavigationTransitionInfo()
                : new EntranceNavigationTransitionInfo();
            if (!this.RootFrame.Navigate(typeof(MainPage), null, transition))
            {
                throw new InvalidOperationException("The usage page could not be opened.");
            }
        }

        firstRunPage?.Dispose();
        this.TrayIcon.Visibility = Visibility.Visible;
        if (!this.TrayIcon.IsCreated)
        {
            this.TrayIcon.ForceCreate(enablesEfficiencyMode: false);
        }

        if (providerId is ProviderId provider)
        {
            this.SelectProvider(provider);
        }
    }

    internal void SelectProvider(ProviderId providerId)
    {
        if (this.RootFrame.Content is MainPage page)
        {
            page.SelectProvider(providerId);
        }
    }

    private void InitialiseThemeSettings(App app)
    {
        try
        {
            ThemeSettings themeSettings = ThemeSettings.CreateForWindowId(this.AppWindow.Id);
            themeSettings.Changed += this.ThemeSettings_Changed;
            this._themeSettings = themeSettings;
            app.SetHighContrastEnabled(themeSettings.HighContrast);
        }
        catch (COMException)
        {
            // XAML theme resources still follow Windows high contrast if notifications are unavailable.
        }
    }

    private void ThemeSettings_Changed(ThemeSettings sender, object args) =>
        ((App)Application.Current).SetHighContrastEnabled(sender.HighContrast);

    private void ToggleWindow()
    {
        if (this.AppWindow.IsVisible)
        {
            this.AppWindow.Hide();
            return;
        }

        this.ShowWindow();
    }

    private void ShowWindow()
    {
        this.AppWindow.Show();
        this.Activate();
    }

    private void ShowSettings() =>
        ((App)Application.Current).ShowSettingsWindow();

    private Task RefreshAllAsync()
    {
        if (this.RootFrame.Content is MainPage page)
        {
            return page.RefreshAllAsync();
        }

        return Task.CompletedTask;
    }

    private void ExitApplication()
    {
        App app = (App)Application.Current;
        _ = this.DispatcherQueue.TryEnqueue(app.Shutdown);
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (this._isExiting)
        {
            return;
        }

        if (this._isFirstRun)
        {
            ((App)Application.Current).Shutdown();
        }
        else
        {
            args.Cancel = true;
            sender.Hide();
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange)
        {
            WindowSizing.UpdateMinimumSize(this, this._minimumWidth, this._minimumHeight);
        }
    }

    private void App_SettingsChanged(AppSettings settings) =>
        this.RefreshAppearance(settings);

    private void RootLayout_ActualThemeChanged(FrameworkElement sender, object args) =>
        App.ApplyCaptionButtonColours(this, this.RootLayout);

    private void SetMinimumSize(double width, double height)
    {
        this._minimumWidth = width;
        this._minimumHeight = height;
        WindowSizing.UpdateMinimumSize(this, width, height);
    }

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }

    private sealed class AsyncRelayCommand(Func<Task> execute) : ICommand
    {
        private bool _isExecuting;

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => !this._isExecuting;

        public async void Execute(object? parameter)
        {
            if (!this.CanExecute(parameter))
            {
                return;
            }

            this._isExecuting = true;
            this.CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                await execute();
            }
            finally
            {
                this._isExecuting = false;
                this.CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
