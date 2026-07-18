using System.Runtime.InteropServices;
using System.Windows.Input;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace UsageDeck.App;

public sealed partial class MainWindow : Window
{
    private bool _isExiting;
    private ThemeSettings? _themeSettings;

    public MainWindow()
    {
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
        WindowSizing.Configure(this, 500, 780, 400, 520);
        this.AppWindow.Changed += this.AppWindow_Changed;
        this.AppWindow.Closing += this.AppWindow_Closing;

        this.RootFrame.Navigate(typeof(MainPage));
    }

    public ICommand ToggleWindowCommand { get; }

    public ICommand ShowWindowCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand SettingsCommand { get; }

    public ICommand ExitCommand { get; }

    internal void PrepareForShutdown()
    {
        this._isExiting = true;
        if (this._themeSettings is not null)
        {
            this._themeSettings.Changed -= this.ThemeSettings_Changed;
        }

        this.TrayIcon.Dispose();
    }

    internal void RefreshAppearance(AppSettings settings)
    {
        App.ApplyWindowAppearance(this, this.RootLayout, this.SolidBackground, settings);
        App.ApplyCaptionButtonColours(this, this.RootLayout);
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
        if (!this._isExiting)
        {
            args.Cancel = true;
            sender.Hide();
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange)
        {
            WindowSizing.UpdateMinimumSize(this, 400, 520);
        }
    }

    private void App_SettingsChanged(AppSettings settings) =>
        App.ApplyWindowAppearance(this, this.RootLayout, this.SolidBackground, settings);

    private void RootLayout_ActualThemeChanged(FrameworkElement sender, object args) =>
        App.ApplyCaptionButtonColours(this, this.RootLayout);

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
