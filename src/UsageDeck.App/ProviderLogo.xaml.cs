using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace UsageDeck.App;

public sealed partial class ProviderLogo : UserControl
{
    public static readonly DependencyProperty ProviderKeyProperty = DependencyProperty.Register(
        nameof(ProviderKey),
        typeof(string),
        typeof(ProviderLogo),
        new PropertyMetadata(string.Empty, OnProviderKeyChanged));

    public ProviderLogo()
    {
        this.InitializeComponent();
        this.UpdateVisibleMark();
    }

    public string ProviderKey
    {
        get => (string)this.GetValue(ProviderKeyProperty);
        set => this.SetValue(ProviderKeyProperty, value);
    }

    private static void OnProviderKeyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((ProviderLogo)sender).UpdateVisibleMark();

    private void UpdateVisibleMark()
    {
        this.AllMark.Visibility = Visibility.Collapsed;
        this.OpenAiMark.Visibility = Visibility.Collapsed;
        this.AnthropicMark.Visibility = Visibility.Collapsed;
        this.AntigravityMark.Visibility = Visibility.Collapsed;
        this.CopilotMark.Visibility = Visibility.Collapsed;
        this.KiroMark.Visibility = Visibility.Collapsed;
        this.AmpMark.Visibility = Visibility.Collapsed;
        this.OpenCodeGoMark.Visibility = Visibility.Collapsed;
        this.ZaiMark.Visibility = Visibility.Collapsed;

        FrameworkElement mark = this.ProviderKey.ToLowerInvariant() switch
        {
            "all" => this.AllMark,
            "claude" => this.AnthropicMark,
            "antigravity" => this.AntigravityMark,
            "copilot" => this.CopilotMark,
            "kiro" => this.KiroMark,
            "amp" => this.AmpMark,
            "opencode-go" => this.OpenCodeGoMark,
            "zai" => this.ZaiMark,
            _ => this.OpenAiMark,
        };
        mark.Visibility = Visibility.Visible;
    }
}
