using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Steaming.Application.ViewModels;
using Steaming.Core.Services;
using Windows.UI;

namespace Steaming.WinUI.Pages;

public sealed partial class ChatPage : Page
{
    private MainViewModel?    _vm;
    private ChatOverlayConfig? _config;
    private bool _loading;

    // Hardcoded sample messages covering different message types
    private static readonly (string Platform, string Username, string Color, string Message)[] Samples =
    [
        ("Twitch", "StreamFriend",  "#9B59B6", "That was an incredible play!"),
        ("Kick",   "KickViewer",    "#2ECC71", "PogChamp amazing! Let's go!"),
        ("Twitch", "ModeratorBob",  "#E74C3C", "!clip — saving that for the highlights"),
        ("Twitch", "SubGifter",     "#F39C12", "Just gifted 5 subs to the community!"),
        ("Kick",   "NewViewer",     "#3498DB", "First time watching, love the content"),
        ("Twitch", "StreamFriend",  "#9B59B6", "Can you show us how you did that?"),
        ("Twitch", "ChatLurker",    "#1ABC9C", "LUL LUL LUL"),
        ("Kick",   "KickViewer",    "#2ECC71", "This is genuinely the best stream I've watched all week"),
    ];

    public ChatPage() { InitializeComponent();  WireChangeHandlers(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not MainViewModel vm) return;
        _vm = vm;
        var profiles = vm.GetChatOverlayProfileNames(vm.PluginChatSources);
        ProfilePicker.ItemsSource = profiles;
        if (profiles.Count > 0)
            ProfilePicker.SelectedIndex = 0;
        else
        {
            _config = vm.GetChatOverlayConfig();
            PopulateFromConfig(_config);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SETTINGS
    // ═══════════════════════════════════════════════════════════════════════

    private void ProfilePicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || ProfilePicker.SelectedItem is not string source) return;
        _config = _vm.SelectChatOverlayProfile(source);
        PopulateFromConfig(_config);
    }

    private void PopulateFromConfig(ChatOverlayConfig cfg)
    {
        _loading = true;

        // Colors
        var bg = HexOrDefault(cfg.BackgroundColor);
        BgColorPicker.Color = bg;
        BgColorSwatch.Background = new SolidColorBrush(bg);
        var text = HexOrDefault(cfg.TextColor);
        TextColorPicker.Color = text;
        TextColorSwatch.Background = new SolidColorBrush(text);
        var bits = HexOrDefault(cfg.BitsColor);
        BitsColorPicker.Color = bits;
        BitsColorSwatch.Background = new SolidColorBrush(bits);
        BgOpacitySlider.Value = Math.Clamp(cfg.BackgroundOpacity, 0, 255);
        BgOpacityValue.Text = ((int)BgOpacitySlider.Value).ToString();

        // Display
        ShowMsgsCheck.IsChecked        = cfg.ShowChatMessages;
        ShowTimestampsCheck.IsChecked   = cfg.ShowTimestamps;
        ShowPlatformCheck.IsChecked     = cfg.ShowPlatformIcon;
        DisappearCheck.IsChecked        = cfg.DisappearMessages;
        FadeCheck.IsChecked             = cfg.FadeMessages;
        DisappearBox.Text               = cfg.DisappearAfterSeconds.ToString();
        FadeBox.Text                    = cfg.FadeSeconds.ToString();
        PlatformFilterPicker.SelectedIndex = PlatformFilterToIndex(cfg.PlatformFilter);
        NameColorModePicker.SelectedIndex  = NameColorModeToIndex(cfg.DisplayNameColorMode);
        MessageStylePicker.SelectedIndex   = string.Equals(cfg.MessageStyle, "TopDown", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        BadgePlacementPicker.SelectedIndex = string.Equals(cfg.BadgePlacement, "AfterUsername", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        MaxLinesBox.Text                = cfg.MaxLinesShown.ToString();

        // Font
        FontFamilyBox.Text              = cfg.FontFamily;
        FontSizeBox.Text                = cfg.FontSize.ToString();
        FontWeightPicker.SelectedIndex  = cfg.FontWeight >= 700 ? 1 : 0;
        TextAlignPicker.SelectedIndex   = TextAlignToIndex(cfg.TextAlign);

        // Layout
        MarginBox.Text                  = cfg.Margin.ToString();
        PaddingBox.Text                 = cfg.MessagePadding.ToString();
        LineSpacingBox.Text             = cfg.LineSpacing.ToString();
        TextShadowBox.Text              = cfg.TextShadow.ToString();
        OutlineBox.Text                 = cfg.OutlineSize.ToString();

        _loading = false;
        RenderPreview(cfg);
    }

    private ChatOverlayConfig BuildConfig()
    {
        var cfg = _config ?? _vm!.GetChatOverlayConfig();

        // Colors
        cfg.BackgroundColor   = ColorToHex(BgColorPicker.Color);
        cfg.TextColor         = ColorToHex(TextColorPicker.Color);
        cfg.BitsColor         = ColorToHex(BitsColorPicker.Color);
        cfg.BackgroundOpacity = (int)BgOpacitySlider.Value;

        // Display
        cfg.ShowChatMessages  = ShowMsgsCheck.IsChecked == true;
        cfg.ShowTimestamps    = ShowTimestampsCheck.IsChecked == true;
        cfg.ShowPlatformIcon  = ShowPlatformCheck.IsChecked == true;
        cfg.DisappearMessages = DisappearCheck.IsChecked == true;
        cfg.FadeMessages      = FadeCheck.IsChecked == true;
        if (int.TryParse(DisappearBox.Text, out var d))  cfg.DisappearAfterSeconds = d;
        if (int.TryParse(FadeBox.Text, out var fade))    cfg.FadeSeconds = fade;
        cfg.PlatformFilter       = IndexToPlatformFilter(PlatformFilterPicker.SelectedIndex);
        cfg.DisplayNameColorMode = IndexToNameColorMode(NameColorModePicker.SelectedIndex);
        cfg.MessageStyle         = MessageStylePicker.SelectedIndex == 1 ? "TopDown" : "BottomUp";
        cfg.BadgePlacement       = BadgePlacementPicker.SelectedIndex == 1 ? "AfterUsername" : "BeforeUsername";
        if (int.TryParse(MaxLinesBox.Text, out var ml))  cfg.MaxLinesShown = ml;

        // Font
        cfg.FontFamily = string.IsNullOrWhiteSpace(FontFamilyBox.Text) ? "Segoe UI" : FontFamilyBox.Text.Trim();
        if (int.TryParse(FontSizeBox.Text, out var fs))  cfg.FontSize = Math.Clamp(fs, 8, 72);
        cfg.FontWeight = FontWeightPicker.SelectedIndex == 1 ? 700 : 400;
        cfg.TextAlign  = IndexToTextAlign(TextAlignPicker.SelectedIndex);

        // Layout
        if (int.TryParse(MarginBox.Text, out var m))      cfg.Margin = m;
        if (int.TryParse(PaddingBox.Text, out var p))     cfg.MessagePadding = p;
        if (int.TryParse(LineSpacingBox.Text, out var ls)) cfg.LineSpacing = ls;
        if (int.TryParse(TextShadowBox.Text, out var sh)) cfg.TextShadow = sh;
        if (int.TryParse(OutlineBox.Text, out var ol))    cfg.OutlineSize = ol;
        return cfg;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var cfg = BuildConfig();
        RenderPreview(cfg);
        var sent = await _vm.SaveAndSendChatOverlayAsync(cfg);
        if (!sent)
        {
            var dlg = new ContentDialog
            {
                Title           = "Not connected",
                Content         = "OBS plugin pipe is not connected. Settings saved locally.",
                CloseButtonText = "OK",
                XamlRoot        = XamlRoot,
            };
            await dlg.ShowAsync();
        }
    }

    private async void ClearObsChat_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var sent = await _vm.ClearObsChatAsync();
        if (!sent)
        {
            var dlg = new ContentDialog
            {
                Title           = "Not connected",
                Content         = "OBS plugin pipe is not connected. Nothing was cleared.",
                CloseButtonText = "OK",
                XamlRoot        = XamlRoot,
            };
            await dlg.ShowAsync();
        }
    }

    private async void SendTest_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var sent = await _vm.SendTestChatMessagesAsync();
        if (!sent)
        {
            var dlg = new ContentDialog
            {
                Title           = "Not connected",
                Content         = "OBS plugin pipe is not connected. Start OBS with the Streaming plugin loaded, then try again.",
                CloseButtonText = "OK",
                XamlRoot        = XamlRoot,
            };
            await dlg.ShowAsync();
        }
    }

    // ── Live update: each field change re-renders the preview ────────────────

    private void WireChangeHandlers()
    {
        FontFamilyBox.TextChanged   += (_, _) => LiveUpdate();
        FontSizeBox.TextChanged     += (_, _) => LiveUpdate();
        MarginBox.TextChanged       += (_, _) => LiveUpdate();
        PaddingBox.TextChanged      += (_, _) => LiveUpdate();
        LineSpacingBox.TextChanged  += (_, _) => LiveUpdate();
        DisappearBox.TextChanged    += (_, _) => LiveUpdate();
        TextShadowBox.TextChanged   += (_, _) => LiveUpdate();
        OutlineBox.TextChanged      += (_, _) => LiveUpdate();
        MaxLinesBox.TextChanged     += (_, _) => LiveUpdate();
        FontWeightPicker.SelectionChanged   += (_, _) => LiveUpdate();
        TextAlignPicker.SelectionChanged    += (_, _) => LiveUpdate();
        NameColorModePicker.SelectionChanged += (_, _) => LiveUpdate();
        MessageStylePicker.SelectionChanged += (_, _) => LiveUpdate();
        ShowTimestampsCheck.Click   += (_, _) => LiveUpdate();
        ShowPlatformCheck.Click     += (_, _) => LiveUpdate();
        ShowMsgsCheck.Click         += (_, _) => LiveUpdate();
    }

    private void BgColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        BgColorSwatch.Background = new SolidColorBrush(args.NewColor);
        LiveUpdate();
    }

    private void TextColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        TextColorSwatch.Background = new SolidColorBrush(args.NewColor);
        LiveUpdate();
    }

    private void BitsColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        BitsColorSwatch.Background = new SolidColorBrush(args.NewColor);
        LiveUpdate();
    }

    private void BgOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (BgOpacityValue != null)
            BgOpacityValue.Text = ((int)e.NewValue).ToString();
        LiveUpdate();
    }

    private void LiveUpdate()
    {
        if (_loading || _vm == null) return;
        RenderPreview(BuildConfig());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // CHAT PREVIEW RENDER
    // ═══════════════════════════════════════════════════════════════════════

    private void RenderPreview(ChatOverlayConfig cfg)
    {
        PreviewSourceLabel.Text = string.IsNullOrWhiteSpace(cfg.SourceName) ? "" : $"— {cfg.SourceName}";

        // Apply background
        if (TryParseColor(cfg.BackgroundColor, out var bgColor))
        {
            byte alpha = (byte)Math.Clamp(cfg.BackgroundOpacity, 0, 255);
            ChatPreviewBorder.Background = new SolidColorBrush(
                Color.FromArgb(alpha, bgColor.R, bgColor.G, bgColor.B));
        }

        var fontFamily  = new FontFamily(string.IsNullOrWhiteSpace(cfg.FontFamily) ? "Segoe UI" : cfg.FontFamily);
        double fontSize = Math.Clamp(cfg.FontSize, 8, 72);
        var fontWeight  = cfg.FontWeight >= 700 ? FontWeights.Bold : FontWeights.Normal;
        int padding     = Math.Max(0, cfg.MessagePadding);
        int lineSpacing = Math.Max(0, cfg.LineSpacing);
        var textAlignment = cfg.TextAlign switch
        {
            "Center" => TextAlignment.Center,
            "Right"  => TextAlignment.Right,
            _        => TextAlignment.Left,
        };

        TryParseColor(cfg.TextColor, out var textColor);

        var panel = new StackPanel { Spacing = lineSpacing };

        foreach (var (platform, username, userColor, message) in Samples)
        {
            // RichTextBlock + Paragraph required for InlineUIContainer (pill border) in WinUI 3.
            // TextBlock does not support InlineUIContainer — only RichTextBlock does.
            var paragraph = new Paragraph();
            var row = new RichTextBlock
            {
                TextWrapping  = TextWrapping.Wrap,
                TextAlignment = textAlignment,
                Margin        = new Thickness(cfg.Margin, padding / 2, cfg.Margin, padding / 2),
                FontFamily    = fontFamily,
                FontSize      = fontSize,
                FontWeight    = fontWeight,
            };
            row.Blocks.Add(paragraph);

            // Platform pill — InlineUIContainer embeds the Border inside flowing text
            if (cfg.ShowPlatformIcon)
            {
                var pillBorder = new Border
                {
                    Background   = new SolidColorBrush(platform == "Twitch"
                        ? Color.FromArgb(255, 100, 65, 165) : Color.FromArgb(255, 83, 252, 30)),
                    CornerRadius = new CornerRadius(3),
                    Padding      = new Thickness(3, 1, 3, 1),
                };
                pillBorder.Child = new TextBlock
                {
                    Text       = platform == "Twitch" ? "T" : "K",
                    FontSize   = Math.Max(8, fontSize - 4),
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Colors.White),
                };
                paragraph.Inlines.Add(new InlineUIContainer { Child = pillBorder });
                paragraph.Inlines.Add(new Run { Text = " " });
            }

            // Timestamp
            if (cfg.ShowTimestamps)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text       = "12:34 ",
                    FontSize   = Math.Max(8, fontSize - 3),
                    Foreground = new SolidColorBrush(Color.FromArgb(160, textColor.R, textColor.G, textColor.B)),
                });
            }

            // Username — colour follows the Display name color mode
            TryParseColor(userColor, out var uColor);
            var nameColor = cfg.DisplayNameColorMode switch
            {
                "PlatformColor" => platform == "Twitch"
                    ? Color.FromArgb(255, 145, 70, 255) : Color.FromArgb(255, 83, 252, 30),
                "BaseTextColor" => textColor,
                _               => uColor,
            };
            paragraph.Inlines.Add(new Run
            {
                Text       = username + ": ",
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(nameColor),
            });

            // Message
            paragraph.Inlines.Add(new Run
            {
                Text       = message,
                Foreground = new SolidColorBrush(textColor),
            });

            panel.Children.Add(row);
        }

        ChatPreviewList.ItemsSource = null;
        ChatPreviewList.Items.Clear();
        ChatPreviewList.Items.Add(panel);
    }

    private static Color HexOrDefault(string? hex)
        => TryParseColor(hex, out var c) ? c : Color.FromArgb(255, 0, 0, 0);

    private static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static bool TryParseColor(string? hex, out Color color)
    {
        color = Color.FromArgb(225, 225, 225, 225);
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.TrimStart('#');
        try
        {
            if (hex.Length == 6)
            {
                color = Color.FromArgb(255,
                    Convert.ToByte(hex[..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16));
                return true;
            }
            if (hex.Length == 8)
            {
                color = Color.FromArgb(
                    Convert.ToByte(hex[..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16),
                    Convert.ToByte(hex[6..8], 16));
                return true;
            }
        }
        catch { }
        return false;
    }

    private static int PlatformFilterToIndex(string? value) => value switch
    {
        "Twitch" => 1, "Kick" => 2, "YouTube" => 3, _ => 0,
    };

    private static string IndexToPlatformFilter(int index) => index switch
    {
        1 => "Twitch", 2 => "Kick", 3 => "YouTube", _ => "All",
    };

    private static int NameColorModeToIndex(string? value) => value switch
    {
        "PlatformColor" => 1, "BaseTextColor" => 2, _ => 0,
    };

    private static string IndexToNameColorMode(int index) => index switch
    {
        1 => "PlatformColor", 2 => "BaseTextColor", _ => "UserColor",
    };

    private static int TextAlignToIndex(string? value) => value switch
    {
        "Center" => 1, "Right" => 2, _ => 0,
    };

    private static string IndexToTextAlign(int index) => index switch
    {
        1 => "Center", 2 => "Right", _ => "Left",
    };
}
