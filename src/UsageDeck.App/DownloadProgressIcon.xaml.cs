using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace UsageDeck.App;

public sealed partial class DownloadProgressIcon : UserControl
{
    public static readonly DependencyProperty DisplayedProgressProperty = DependencyProperty.Register(
        nameof(DisplayedProgress), typeof(double), typeof(DownloadProgressIcon),
        new PropertyMetadata(0d, OnDisplayedProgressChanged));

    private readonly UISettings _uiSettings = new();
    private Storyboard? _progressAnimation;
    private int _targetProgress;

    public DownloadProgressIcon()
    {
        this.InitializeComponent();
        this.Unloaded += this.Icon_Unloaded;
    }

    public double DisplayedProgress
    {
        get => (double)this.GetValue(DisplayedProgressProperty);
        set => this.SetValue(DisplayedProgressProperty, value);
    }

    internal void SetProgress(int? percentage)
    {
        int progress = Math.Clamp(percentage ?? 0, 0, 100);
        this.AvailableDot.Visibility = percentage.HasValue ? Visibility.Collapsed : Visibility.Visible;
        this.ProgressTrack.Visibility = percentage.HasValue ? Visibility.Visible : Visibility.Collapsed;

        if (!percentage.HasValue || progress is 0 or 100 || !this.IsLoaded || !this._uiSettings.AnimationsEnabled)
        {
            this._targetProgress = progress;
            this.StopProgressAnimation();
            this.DisplayedProgress = progress;
            this.DrawProgress(progress);
            return;
        }

        if (progress == this._targetProgress)
        {
            return;
        }

        double from = this.DisplayedProgress;
        this.StopProgressAnimation();
        this._targetProgress = progress;
        // Set the final base value so completion does not revert the arc to its starting position.
        this.DisplayedProgress = progress;
        DoubleAnimation transition = new()
        {
            From = from,
            To = progress,
            Duration = new Duration(TimeSpan.FromMilliseconds(150)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
            FillBehavior = FillBehavior.Stop,
        };
        Storyboard.SetTarget(transition, this);
        Storyboard.SetTargetProperty(transition, nameof(DisplayedProgress));
        this._progressAnimation = new Storyboard();
        this._progressAnimation.Children.Add(transition);
        this._progressAnimation.Begin();
    }

    private static void OnDisplayedProgressChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((DownloadProgressIcon)sender).DrawProgress((double)args.NewValue);

    private void DrawProgress(double progress)
    {
        this.ProgressTrack.Opacity = progress < 100 ? 0.3 : 1;
        this.ProgressArc.Visibility = this.ProgressTrack.Visibility == Visibility.Visible && progress is > 0 and < 100
            ? Visibility.Visible : Visibility.Collapsed;
        double angle = 2 * Math.PI * progress / 100;
        this.ProgressSegment.Point = new Point(15 + 14 * Math.Sin(angle), 15 - 14 * Math.Cos(angle));
        this.ProgressSegment.IsLargeArc = progress > 50;
    }

    private void StopProgressAnimation()
    {
        this._progressAnimation?.Stop();
        this._progressAnimation = null;
    }

    private void Icon_Unloaded(object sender, RoutedEventArgs args)
    {
        this.StopProgressAnimation();
        this.DisplayedProgress = this._targetProgress;
    }
}
