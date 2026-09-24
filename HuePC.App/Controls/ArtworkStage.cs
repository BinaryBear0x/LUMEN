using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HuePC.App.Controls;

/// <summary>A transparent surface for the page artwork so the illustrations sit directly on the window background.</summary>
public sealed class ArtworkStage : Border
{
    private readonly PageMotif _motif;

    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(string), typeof(ArtworkStage), new PropertyMetadata("Control", OnPageChanged));

    public string Page
    {
        get => (string)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public ArtworkStage()
    {
        CornerRadius = new CornerRadius(22);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        ClipToBounds = true;
        IsHitTestVisible = false;

        _motif = new PageMotif { Margin = new Thickness(16, 12, 16, 12) };
        Child = _motif;
    }

    private static void OnPageChanged(DependencyObject target, DependencyPropertyChangedEventArgs args) =>
        ((ArtworkStage)target)._motif.Page = (string)args.NewValue;
}
