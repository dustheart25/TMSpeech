using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TMSpeech.Core;

namespace TMSpeech.GUI.Views;

public partial class CaptionView : UserControl
{
    public CaptionView()
    {
        InitializeComponent();
    }

    public static readonly StyledProperty<Color> ShadowColorProperty = AvaloniaProperty.Register<CaptionView, Color>(
        "ShadowColor", Colors.Black);

    public Color ShadowColor
    {
        get => GetValue(ShadowColorProperty);
        set => SetValue(ShadowColorProperty, value);
    }

    public static readonly StyledProperty<int> ShadowSizeProperty = AvaloniaProperty.Register<CaptionView, int>(
        "ShadowSize", 10);

    public int ShadowSize
    {
        get => GetValue(ShadowSizeProperty);
        set => SetValue(ShadowSizeProperty, value);
    }

    public static readonly StyledProperty<Color> FontColorProperty = AvaloniaProperty.Register<CaptionView, Color>(
        "FontColor", Colors.White);

    public Color FontColor
    {
        get => GetValue(FontColorProperty);
        set => SetValue(FontColorProperty, value);
    }

    public static readonly StyledProperty<Color> CorrectedFontColorProperty =
        AvaloniaProperty.Register<CaptionView, Color>(
            "CorrectedFontColor", Colors.LightSkyBlue);

    public Color CorrectedFontColor
    {
        get => GetValue(CorrectedFontColorProperty);
        set => SetValue(CorrectedFontColorProperty, value);
    }

    public static readonly StyledProperty<Color> TranslationFontColorProperty =
        AvaloniaProperty.Register<CaptionView, Color>(
            "TranslationFontColor", Colors.Yellow);

    public Color TranslationFontColor
    {
        get => GetValue(TranslationFontColorProperty);
        set => SetValue(TranslationFontColorProperty, value);
    }

    public static readonly StyledProperty<TextAlignment> TextAlignProperty =
        AvaloniaProperty.Register<CaptionView, TextAlignment>(
            "TextAlign", TextAlignment.Left);

    public TextAlignment TextAlign
    {
        get => GetValue(TextAlignProperty);
        set => SetValue(TextAlignProperty, value);
    }

    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<CaptionView, string>(
        "Text");

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly StyledProperty<IEnumerable<CaptionTextInfo>> CaptionLinesProperty =
        AvaloniaProperty.Register<CaptionView, IEnumerable<CaptionTextInfo>>(
            "CaptionLines", Array.Empty<CaptionTextInfo>());

    public IEnumerable<CaptionTextInfo> CaptionLines
    {
        get => GetValue(CaptionLinesProperty);
        set => SetValue(CaptionLinesProperty, value);
    }
}
