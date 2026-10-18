using System;
using CommunityToolkit.Mvvm.ComponentModel;
using JitenMPV.Core.Config;
using JitenMPV.Core.Rendering;
using JitenMPV.Core.Theming;

namespace JitenMPV.App.ViewModels;

public partial class StateStyleViewModel : ViewModelBase
{
    public string DisplayName { get; }

    [ObservableProperty] private string _textColor = "#ffffff";
    [ObservableProperty] private string _outlineColor = "#000000";
    [ObservableProperty] private string _shadowColor = "#000000";
    [ObservableProperty] private bool _hasShadow;

    [ObservableProperty] private double _outlineSize = 3;
    [ObservableProperty] private double _shadowDepth = 1;

    [ObservableProperty] private int _textOpacityPercent = 100;

    [ObservableProperty] private bool _bold;
    [ObservableProperty] private bool _italic;
    [ObservableProperty] private bool _underline;
    [ObservableProperty] private bool _strikethrough;

    [ObservableProperty] private string _underlineColor = "#ffffff";
    [ObservableProperty] private double _underlineThickness = UnderlineBarRenderer.DefaultThickness;

    [ObservableProperty] private string _swatchColor = "#ffffff";

    /// Kept so an untouched style saves what it was opened with: the percent slider cannot hold
    /// every 0-255 opacity, and a plain underline would otherwise come back as a coloured bar.
    private int? _loadedTextOpacity;
    private bool _underlineFollowsText;

    public StateStyleViewModel(string name)
    {
        DisplayName = name;
    }

    partial void OnTextColorChanged(string value)
    {
        SwatchColor = value;
        if (_underlineFollowsText)
            UnderlineColor = value;
    }

    partial void OnUnderlineColorChanged(string value)
        => _underlineFollowsText &= string.Equals(value, TextColor, StringComparison.OrdinalIgnoreCase);

    public CustomStateStyle ToCustomStateStyle()
    {
        var opacity = _loadedTextOpacity is { } loaded && ToPercent(loaded) == TextOpacityPercent
            ? loaded
            : TextOpacityPercent < 100
                ? (int?)Math.Round(TextOpacityPercent * 255.0 / 100)
                : null;
        var plainUnderline = !Underline || _underlineFollowsText;

        return new CustomStateStyle
        {
            TextColor = TextColor,
            OutlineColor = OutlineColor,
            ShadowColor = HasShadow ? ShadowColor : null,
            OutlineSize = OutlineSize,
            ShadowDepth = HasShadow ? ShadowDepth : null,
            TextOpacity = opacity,
            Bold = Bold,
            Italic = Italic,
            Underline = Underline,
            UnderlineColor = plainUnderline ? null : UnderlineColor,
            UnderlineThickness = plainUnderline ? null : UnderlineThickness,
            Strikethrough = Strikethrough,
        };
    }

    private static int ToPercent(int opacity) => (int)Math.Round(opacity * 100.0 / 255);

    public static StateStyleViewModel FromCustomStateStyle(string name, CustomStateStyle css)
    {
        var vm = new StateStyleViewModel(name)
        {
            TextColor = css.TextColor,
            OutlineColor = css.OutlineColor,
            ShadowColor = css.ShadowColor ?? "#000000",
            HasShadow = css.ShadowColor is not null,
            OutlineSize = css.OutlineSize,
            ShadowDepth = css.ShadowDepth ?? 1,
            TextOpacityPercent = css.TextOpacity is { } op ? ToPercent(op) : 100,
            Bold = css.Bold,
            Italic = css.Italic,
            Underline = css.Underline,
            // A preset underline is drawn in the text colour, so that is what the picker starts from
            // when a style that never named an underline colour is opened for editing.
            UnderlineColor = css.UnderlineColor ?? css.TextColor,
            UnderlineThickness = css.UnderlineThickness ?? UnderlineBarRenderer.DefaultThickness,
            Strikethrough = css.Strikethrough,
        };
        vm.SwatchColor = vm.TextColor;
        vm._loadedTextOpacity = css.TextOpacity;
        vm._underlineFollowsText = css.UnderlineColor is null;
        return vm;
    }

    public static StateStyleViewModel FromWordStyleState(string name, WordStyleState ws)
        => FromCustomStateStyle(name, CustomStateStyle.FromWordStyleState(ws));
}
