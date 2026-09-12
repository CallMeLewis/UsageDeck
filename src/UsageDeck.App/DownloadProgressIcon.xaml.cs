using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace UsageDeck.App;

public sealed partial class DownloadProgressIcon : UserControl
{
    public DownloadProgressIcon()
    {
        this.InitializeComponent();
    }

    internal void SetProgress(int? percentage)
    {
        int progress = Math.Clamp(percentage ?? 0, 0, 100);
        this.AvailableArrow.Visibility = percentage.HasValue ? Visibility.Collapsed : Visibility.Visible;
        this.AvailableDot.Visibility = percentage.HasValue ? Visibility.Collapsed : Visibility.Visible;
        this.DownloadArrow.Visibility = percentage.HasValue ? Visibility.Visible : Visibility.Collapsed;
        this.ProgressTrack.Visibility = percentage.HasValue ? Visibility.Visible : Visibility.Collapsed;
        this.ProgressTrack.Opacity = percentage.HasValue && progress < 100 ? 0.3 : 1;
        this.ProgressArc.Visibility = progress is > 0 and < 100 ? Visibility.Visible : Visibility.Collapsed;
        double angle = 2 * Math.PI * progress / 100;
        this.ProgressSegment.Point = new Point(12 + 10 * Math.Sin(angle), 12 - 10 * Math.Cos(angle));
        this.ProgressSegment.IsLargeArc = progress > 50;
    }
}
