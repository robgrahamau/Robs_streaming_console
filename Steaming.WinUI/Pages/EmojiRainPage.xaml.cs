using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Steaming.Application.ViewModels;
using Steaming.Core.Services;

namespace Steaming.WinUI.Pages;

public sealed partial class EmojiRainPage : Page
{
    private MainViewModel? _vm;
    private bool _loading;

    public EmojiRainPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _vm = e.Parameter as MainViewModel;
        if (_vm == null) return;
        LoadSettings();
    }

    private void LoadSettings()
    {
        if (_vm == null) return;
        _loading = true;
        var cfg = _vm.GetEmojiRainConfig();

        ErOnFollow.IsChecked    = cfg.TriggerOnFollow;
        ErFollowEmoji.Text      = cfg.FollowEmojis;
        ErFollowGif.Text        = cfg.FollowGif ?? "";

        ErOnSubscribe.IsChecked = cfg.TriggerOnSubscribe;
        ErSubscribeEmoji.Text   = cfg.SubscribeEmojis;
        ErSubscribeGif.Text     = cfg.SubscribeGif ?? "";

        ErOnBits.IsChecked      = cfg.TriggerOnBits;
        ErBitsEmoji.Text        = cfg.BitsEmojis;
        ErBitsGif.Text          = cfg.BitsGif ?? "";

        ErOnRaid.IsChecked      = cfg.TriggerOnRaid;
        ErRaidEmoji.Text        = cfg.RaidEmojis;
        ErRaidGif.Text          = cfg.RaidGif ?? "";

        ErSize.Text   = cfg.EmojiSize.ToString();
        ErSpeed.Text  = cfg.FallSpeed.ToString();
        ErLife.Text   = cfg.ParticleLifeSec.ToString();
        ErMax.Text    = cfg.MaxParticles.ToString();
        ErCount.Text  = cfg.CountPerTrigger.ToString();
        ErSpread.Text = cfg.Spread.ToString();
        ErFadeOut.IsChecked = cfg.FadeOut;
        ErSpin.IsChecked    = cfg.Spin;

        _loading = false;
    }

    private EmojiRainConfig BuildConfig()
    {
        static int ParseInt(string s, int def, int min, int max)
            => Math.Clamp(int.TryParse(s, out var v) ? v : def, min, max);

        return new EmojiRainConfig
        {
            TriggerOnFollow    = ErOnFollow.IsChecked == true,
            FollowEmojis       = ErFollowEmoji.Text,
            FollowGif          = string.IsNullOrWhiteSpace(ErFollowGif.Text) ? null : ErFollowGif.Text,

            TriggerOnSubscribe = ErOnSubscribe.IsChecked == true,
            SubscribeEmojis    = ErSubscribeEmoji.Text,
            SubscribeGif       = string.IsNullOrWhiteSpace(ErSubscribeGif.Text) ? null : ErSubscribeGif.Text,

            TriggerOnBits      = ErOnBits.IsChecked == true,
            BitsEmojis         = ErBitsEmoji.Text,
            BitsGif            = string.IsNullOrWhiteSpace(ErBitsGif.Text) ? null : ErBitsGif.Text,

            TriggerOnRaid      = ErOnRaid.IsChecked == true,
            RaidEmojis         = ErRaidEmoji.Text,
            RaidGif            = string.IsNullOrWhiteSpace(ErRaidGif.Text) ? null : ErRaidGif.Text,

            EmojiSize        = ParseInt(ErSize.Text,  48,  8, 256),
            FallSpeed        = ParseInt(ErSpeed.Text, 400, 50, 2000),
            ParticleLifeSec  = ParseInt(ErLife.Text,  4,  1, 30),
            MaxParticles     = ParseInt(ErMax.Text,   100, 1, 1000),
            CountPerTrigger  = ParseInt(ErCount.Text, 20, 1, 500),
            Spread           = ParseInt(ErSpread.Text, 30, 0, 100),
            FadeOut          = ErFadeOut.IsChecked == true,
            Spin             = ErSpin.IsChecked    == true,
        };
    }

    private void Save_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_vm == null || _loading) return;
        _vm.SaveEmojiRainSettings(BuildConfig());
        SaveStatus.Text = "Saved.";
    }

    private async void TestAll_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.SaveEmojiRainSettings(BuildConfig());
        await _vm.TriggerTestEmojiRainAsync();
    }

    private async void TestFollow_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.SaveEmojiRainSettings(BuildConfig());
        await _vm.TriggerEmojiRainForAsync("Follow");
    }

    private async void TestSubscribe_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.SaveEmojiRainSettings(BuildConfig());
        await _vm.TriggerEmojiRainForAsync("Subscribe");
    }

    private async void TestBits_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.SaveEmojiRainSettings(BuildConfig());
        await _vm.TriggerEmojiRainForAsync("Bits");
    }

    private async void TestRaid_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.SaveEmojiRainSettings(BuildConfig());
        await _vm.TriggerEmojiRainForAsync("Raid");
    }
}
