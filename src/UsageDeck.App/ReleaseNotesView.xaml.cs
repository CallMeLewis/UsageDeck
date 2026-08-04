using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace UsageDeck.App;

public sealed partial class ReleaseNotesView : UserControl
{
    public ReleaseNotesView()
    {
        this.InitializeComponent();
    }

    internal void Present(ReleaseNotesLoadResult result, bool isCompact)
    {
        this.ContentPanel.Children.Clear();
        if (result.Document is not ReleaseNotesDocument document)
        {
            this.ContentPanel.Children.Add(CreateUnavailableState(result.UnavailableMessage));
            return;
        }

        foreach (string paragraph in document.Introduction)
        {
            this.ContentPanel.Children.Add(CreateTextBlock(paragraph, isSecondary: false));
        }

        int shownItems = 0;
        int maximumItems = isCompact ? 3 : int.MaxValue;
        foreach (ReleaseNotesSection section in document.Sections)
        {
            string[] visibleItems = section.Items
                .Take(Math.Max(0, maximumItems - shownItems))
                .ToArray();
            if (visibleItems.Length == 0)
            {
                continue;
            }

            TextBlock heading = CreateTextBlock(section.Heading, isSecondary: false);
            heading.Margin = new Thickness(0, shownItems == 0 ? 2 : 8, 0, 0);
            heading.FontWeight = FontWeights.SemiBold;
            this.ContentPanel.Children.Add(heading);

            foreach (string item in visibleItems)
            {
                this.ContentPanel.Children.Add(CreateBullet(item));
                shownItems++;
            }
        }

        if (!isCompact)
        {
            foreach (string note in document.ClosingNotes)
            {
                TextBlock noteText = CreateTextBlock(note, isSecondary: true);
                noteText.Margin = new Thickness(0, 6, 0, 0);
                this.ContentPanel.Children.Add(noteText);
            }
        }
        else if (document.Sections.Sum(section => section.Items.Count) > shownItems
            || document.ClosingNotes.Count > 0)
        {
            TextBlock moreText = CreateTextBlock(
                "More details are available in Settings.",
                isSecondary: true);
            moreText.Margin = new Thickness(0, 2, 0, 0);
            this.ContentPanel.Children.Add(moreText);
        }
    }

    private static Grid CreateUnavailableState(string message)
    {
        Grid grid = new() { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        FontIcon icon = new()
        {
            FontSize = 16,
            Glyph = "\uE946",
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        Grid.SetColumn(icon, 0);

        TextBlock text = CreateTextBlock(message, isSecondary: true);
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        return grid;
    }

    private static Grid CreateBullet(string markdown)
    {
        Grid grid = new() { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Ellipse marker = new()
        {
            Width = 5,
            Height = 5,
            Margin = new Thickness(1, 7, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Fill = (Brush)Application.Current.Resources["BrandAccentBrush"],
        };
        Grid.SetColumn(marker, 0);

        TextBlock text = CreateTextBlock(markdown, isSecondary: false);
        Grid.SetColumn(text, 1);
        grid.Children.Add(marker);
        grid.Children.Add(text);
        return grid;
    }

    private static TextBlock CreateTextBlock(string markdown, bool isSecondary)
    {
        TextBlock text = new()
        {
            FontSize = 13,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
        };
        if (isSecondary)
        {
            text.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }

        AddInlines(text, markdown);
        return text;
    }

    private static void AddInlines(TextBlock text, string markdown)
    {
        int position = 0;
        while (position < markdown.Length)
        {
            int boldStart = markdown.IndexOf("**", position, StringComparison.Ordinal);
            int codeStart = markdown.IndexOf('`', position);
            int markerStart = SelectFirstMarker(boldStart, codeStart);
            if (markerStart < 0)
            {
                text.Inlines.Add(new Run { Text = markdown[position..] });
                break;
            }

            if (markerStart > position)
            {
                text.Inlines.Add(new Run { Text = markdown[position..markerStart] });
            }

            bool isBold = markerStart == boldStart;
            string marker = isBold ? "**" : "`";
            int contentStart = markerStart + marker.Length;
            int markerEnd = markdown.IndexOf(marker, contentStart, StringComparison.Ordinal);
            if (markerEnd < 0)
            {
                text.Inlines.Add(new Run { Text = markdown[markerStart..] });
                break;
            }

            Run emphasised = new() { Text = markdown[contentStart..markerEnd] };
            if (isBold)
            {
                emphasised.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                emphasised.FontFamily = new FontFamily("Cascadia Mono");
                emphasised.FontSize = 12;
            }

            text.Inlines.Add(emphasised);
            position = markerEnd + marker.Length;
        }
    }

    private static int SelectFirstMarker(int first, int second)
    {
        if (first < 0)
        {
            return second;
        }

        return second < 0 ? first : Math.Min(first, second);
    }
}
