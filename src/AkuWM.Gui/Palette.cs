using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace AkuWM.Gui;

/// <summary>Rosé Pine, the palette the sway desk uses, over Fluent's dark variant.</summary>
public static class Palette
{
    public static readonly Color Base = Color.Parse("#191724");
    public static readonly Color Surface = Color.Parse("#1f1d2e");
    public static readonly Color Overlay = Color.Parse("#26233a");
    public static readonly Color Muted = Color.Parse("#6e6a86");
    public static readonly Color Subtle = Color.Parse("#908caa");
    public static readonly Color Text = Color.Parse("#e0def4");
    public static readonly Color Love = Color.Parse("#eb6f92");
    public static readonly Color Gold = Color.Parse("#f6c177");
    public static readonly Color Rose = Color.Parse("#ebbcba");
    public static readonly Color Pine = Color.Parse("#31748f");
    public static readonly Color Foam = Color.Parse("#9ccfd8");
    public static readonly Color Iris = Color.Parse("#c4a7e7");
    public static readonly Color HighlightLow = Color.Parse("#21202e");
    public static readonly Color HighlightMed = Color.Parse("#403d52");
    public static readonly Color HighlightHigh = Color.Parse("#524f67");

    public static readonly IBrush BaseBrush = new SolidColorBrush(Base);
    public static readonly IBrush SurfaceBrush = new SolidColorBrush(Surface);
    public static readonly IBrush OverlayBrush = new SolidColorBrush(Overlay);
    public static readonly IBrush MutedBrush = new SolidColorBrush(Muted);
    public static readonly IBrush SubtleBrush = new SolidColorBrush(Subtle);
    public static readonly IBrush TextBrush = new SolidColorBrush(Text);
    public static readonly IBrush LoveBrush = new SolidColorBrush(Love);
    public static readonly IBrush GoldBrush = new SolidColorBrush(Gold);
    public static readonly IBrush RoseBrush = new SolidColorBrush(Rose);
    public static readonly IBrush PineBrush = new SolidColorBrush(Pine);
    public static readonly IBrush FoamBrush = new SolidColorBrush(Foam);
    public static readonly IBrush IrisBrush = new SolidColorBrush(Iris);
    public static readonly IBrush HighlightLowBrush = new SolidColorBrush(HighlightLow);
    public static readonly IBrush HighlightMedBrush = new SolidColorBrush(HighlightMed);
    public static readonly IBrush HighlightHighBrush = new SolidColorBrush(HighlightHigh);

    public static void Apply(Application app)
    {
        var r = app.Resources;
        r["SystemAccentColor"] = Iris;
        r["SystemAccentColorDark1"] = Iris;
        r["SystemAccentColorDark2"] = Iris;
        r["SystemAccentColorDark3"] = Iris;
        r["SystemAccentColorLight1"] = Iris;
        r["SystemAccentColorLight2"] = Iris;
        r["SystemAccentColorLight3"] = Iris;
        r["SystemRegionColor"] = Base;
        r["SystemChromeLowColor"] = Surface;
        r["SystemChromeMediumColor"] = Overlay;
        r["SystemChromeMediumLowColor"] = Surface;
        r["SystemChromeHighColor"] = HighlightMed;
        r["SystemBaseHighColor"] = Text;
        r["SystemBaseMediumHighColor"] = Text;
        r["SystemBaseMediumColor"] = Subtle;
        r["SystemBaseMediumLowColor"] = Muted;
        r["SystemBaseLowColor"] = HighlightMed;
        r["SystemAltHighColor"] = Base;
        r["SystemListLowColor"] = HighlightLow;
        r["SystemListMediumColor"] = HighlightMed;
        r["SystemControlBackgroundAltHighBrush"] = SurfaceBrush;
        r["TextControlBackground"] = SurfaceBrush;
        r["TextControlBackgroundPointerOver"] = SurfaceBrush;
        r["TextControlBackgroundFocused"] = SurfaceBrush;
        r["TextControlBorderBrush"] = HighlightMedBrush;
        r["TextControlBorderBrushPointerOver"] = HighlightHighBrush;
        r["ButtonBackground"] = OverlayBrush;
        r["ButtonBackgroundPointerOver"] = HighlightMedBrush;
        r["ButtonBackgroundPressed"] = HighlightHighBrush;
        r["ButtonForeground"] = TextBrush;
        r["ButtonForegroundPointerOver"] = TextBrush;
        r["ButtonForegroundPressed"] = TextBrush;
        r["ComboBoxBackground"] = SurfaceBrush;
        r["ComboBoxBackgroundPointerOver"] = OverlayBrush;
        r["ComboBoxDropDownBackground"] = OverlayBrush;

        app.Styles.Add(new Style(x => x.OfType<TextBlock>()) { Setters = { new Setter(TextBlock.ForegroundProperty, TextBrush) } });
        app.Styles.Add(new Style(x => x.OfType<TextBlock>().Class("dim")) { Setters = { new Setter(TextBlock.ForegroundProperty, SubtleBrush) } });
        app.Styles.Add(new Style(x => x.OfType<TextBlock>().Class("mono")) { Setters = { new Setter(TextBlock.FontFamilyProperty, new FontFamily("Cascadia Mono,Consolas,JetBrains Mono,monospace")) } });
        app.Styles.Add(new Style(x => x.OfType<TextBlock>().Class("h1")) { Setters = { new Setter(TextBlock.FontSizeProperty, 22.0), new Setter(TextBlock.FontWeightProperty, FontWeight.Bold) } });
        app.Styles.Add(new Style(x => x.OfType<TextBlock>().Class("h2")) { Setters = { new Setter(TextBlock.FontSizeProperty, 13.0), new Setter(TextBlock.FontWeightProperty, FontWeight.SemiBold), new Setter(TextBlock.ForegroundProperty, SubtleBrush) } });
        app.Styles.Add(new Style(x => x.OfType<Button>()) { Setters = { new Setter(Button.CornerRadiusProperty, new CornerRadius(6)), new Setter(Button.PaddingProperty, new Thickness(12, 6)) } });
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("accent")) { Setters = { new Setter(Button.BackgroundProperty, IrisBrush), new Setter(Button.ForegroundProperty, BaseBrush) } });
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("danger")) { Setters = { new Setter(Button.BackgroundProperty, LoveBrush), new Setter(Button.ForegroundProperty, BaseBrush) } });
        app.Styles.Add(new Style(x => x.OfType<TextBox>()) { Setters = { new Setter(TextBox.CornerRadiusProperty, new CornerRadius(6)) } });
        app.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(10, 6)), new Setter(ListBoxItem.CornerRadiusProperty, new CornerRadius(6)) } });
    }
}
