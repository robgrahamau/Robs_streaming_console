using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Steaming.Application.Services;
using Steaming.Application.ViewModels;
using Steaming.Core.Configuration;
using Steaming.Core.Models;
using Steaming.Core.Services;

namespace Steaming.WinUI.Pages;

public sealed partial class ChatbotPage : Page
{
    private MainViewModel? _vm;
    private PlatformSessionFlowService? _flows;
    private BotCommand? _editingCommand;
    private BotShout? _editingShout;
    private BotTimer? _editingTimer;
    private string _newCmdSoundPath = "";

    public ChatbotPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not MainViewModel vm) return;
        _vm = vm;
        _flows = App.Services?.GetService<PlatformSessionFlowService>();

        CommandList.ItemsSource = vm.ObservableCommands;
        ShoutList.ItemsSource = vm.ObservableShouts;
        TimerList.ItemsSource = vm.ObservableTimers;
        NewCmdAlertName.ItemsSource = vm.CustomAlertNames;

        FilterLinksCheck.IsChecked = vm.Chatbot.AutoMod.FilterLinks;
        FilterCapsCheck.IsChecked = vm.Chatbot.AutoMod.FilterAllCaps;
        TimeoutCheck.IsChecked = vm.Chatbot.AutoMod.TimeoutOnViolation;
        TimeoutSecsBox.Text = vm.Chatbot.AutoMod.TimeoutSeconds.ToString();
        BlockedWordsBox.Text = string.Join(", ", vm.Chatbot.AutoMod.BlockedWords);

        _loadingAnnounce = true;
        AnnounceLiveCheck.IsChecked = vm.Chatbot.AnnounceLiveEnabled;
        AnnounceLiveMessageBox.Text = vm.Chatbot.AnnounceLiveMessage;
        _loadingAnnounce = false;

        RefreshBotStatus();
    }

    private bool _loadingAnnounce;

    private void AnnounceLive_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm == null || _loadingAnnounce) return;
        _vm.SetAnnounceLive(AnnounceLiveCheck.IsChecked == true, AnnounceLiveMessageBox.Text);
    }

    private void AnnounceLiveMessage_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_vm == null || _loadingAnnounce) return;
        _vm.SetAnnounceLive(AnnounceLiveCheck.IsChecked == true, AnnounceLiveMessageBox.Text);
    }

    private void RefreshBotStatus()
    {
        if (_vm == null) return;

        TwitchBotStatusText.Text = _vm.IsTwitchLoggedIn
            ? $"Sending as {_vm.TwitchUsername}"
            : "Not connected";

        if (_vm.IsKickBotConnected && !string.IsNullOrWhiteSpace(_vm.KickBotUsername))
        {
            KickBotStatusText.Text = $"Connected as {_vm.KickBotUsername}";
            ConnectKickBotBtn.IsEnabled = false;
            DisconnectKickBotBtn.IsEnabled = true;
        }
        else
        {
            KickBotStatusText.Text = "Not connected";
            ConnectKickBotBtn.IsEnabled = true;
            DisconnectKickBotBtn.IsEnabled = false;
        }
    }

    private async void ConnectKickBot_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || _flows == null) return;
        var request = _flows.CreateKickBotLoginRequest();
        if (!request.IsEnabled)
        {
            var dlg = new ContentDialog
            {
                Title = "Kick Bot Login",
                Content = request.DisabledMessage ?? "Kick login is currently disabled.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await dlg.ShowAsync();
            return;
        }

        var auth = App.Services?.GetRequiredService<PlatformAuthConfig>();
        var creds = App.Services?.GetRequiredService<PlatformCredentialService>();
        if (auth == null || creds == null) return;

        var loginWin = new LoginWindow(
            "Login with Kick Bot Account",
            request.AuthUrl!,
            request.RedirectUri!,
            isFragment: false,
            profileName: "KickBot");
        loginWin.Activate();
        var result = await loginWin.WaitForResultAsync();
        if (result.Code == null) return;

        var exchanged = await creds.ExchangeKickCodeAsync(
            result.Code,
            request.CodeVerifier!,
            auth.KickClientId,
            auth.KickClientSecret,
            auth.RedirectUri);
        if (exchanged == null) return;

        var (_, username, _) = await creds.FetchKickUserInfoAsync(exchanged.AccessToken);
        if (string.IsNullOrWhiteSpace(username)) username = "KickBot";

        _vm.ConnectKickBot(exchanged.AccessToken, exchanged.RefreshToken, username);
        RefreshBotStatus();
    }

    private void DisconnectKickBot_Click(object sender, RoutedEventArgs e)
    {
        _vm?.DisconnectKickBot();
        RefreshBotStatus();
    }

    private void AddCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var name = NewCmdName.Text.Trim().TrimStart('!').ToLowerInvariant();
        var response = NewCmdResponse.Text.Trim();
        if (_editingCommand != null && string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(response))
        {
            ResetCommandForm(clearEditing: true);
            CmdFormError.Visibility = Visibility.Collapsed;
            return;
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(response))
        {
            CmdFormError.Text = $"Enter a command name and a response, then click {AddCommandBtn.Content}.";
            CmdFormError.Visibility = Visibility.Visible;
            return;
        }

        CmdFormError.Visibility = Visibility.Collapsed;
        if (_editingCommand != null)
            _vm.RemoveBotCommand(_editingCommand);

        _vm.AddBotCommand(new BotCommand
        {
            Name = name,
            Response = response,
            Target = ParseReplyTarget(NewCmdTarget),
            SoundFile = _newCmdSoundPath,
            ModOnly = NewCmdModOnly.IsChecked == true,
            AllowedUsers = NewCmdAllowedUsers.Text.Trim(),
            CooldownSeconds = int.TryParse(NewCmdCooldown.Text, out var cdSecs) ? Math.Clamp(cdSecs, 0, 3600) : 10,
            AlertEnabled = NewCmdAlertEnabled.IsChecked == true,
            AlertName = NewCmdAlertName.SelectedItem as string ?? "",
        });
        ResetCommandForm(clearEditing: true);
    }

    private async void PickCmdSound_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".wav");
            picker.FileTypeFilter.Add(".ogg");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            SetCommandSound(file.Path);
        }
        catch (Exception ex)
        {
            CmdFormError.Text = $"Could not pick a sound file: {ex.Message}";
            CmdFormError.Visibility = Visibility.Visible;
        }
    }

    private void ClearCmdSound_Click(object sender, RoutedEventArgs e)
    {
        _newCmdSoundPath = "";
        NewCmdSoundLabel.Text = "No sound";
        ClearCmdSoundBtn.Visibility = Visibility.Collapsed;
    }

    private void RemoveCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || CommandList.SelectedItem is not BotCommand cmd) return;
        if (ReferenceEquals(_editingCommand, cmd))
            ResetCommandForm(clearEditing: true);
        _vm.RemoveBotCommand(cmd);
    }

    private void EditCommand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not BotCommand cmd) return;
        _editingCommand = cmd;
        NewCmdName.Text = cmd.Name;
        NewCmdResponse.Text = cmd.Response;
        SetReplyTarget(NewCmdTarget, cmd.Target);
        NewCmdModOnly.IsChecked = cmd.ModOnly;
        NewCmdAllowedUsers.Text = cmd.AllowedUsers ?? "";
        NewCmdCooldown.Text = cmd.CooldownSeconds.ToString();
        SetCommandSound(cmd.SoundFile);
        NewCmdAlertEnabled.IsChecked = cmd.AlertEnabled;
        NewCmdAlertName.SelectedItem = string.IsNullOrWhiteSpace(cmd.AlertName) ? null : cmd.AlertName;
        AddCommandBtn.Content = "Save";
        CmdFormError.Visibility = Visibility.Collapsed;
    }

    private void DeleteCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || (sender as Button)?.Tag is not BotCommand cmd) return;
        if (ReferenceEquals(_editingCommand, cmd))
            ResetCommandForm(clearEditing: true);
        _vm.RemoveBotCommand(cmd);
    }

    private void AddExample_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || (sender as Button)?.Tag is not string tag) return;
        var parts = tag.Split('|', 2);
        if (parts.Length != 2) return;
        _vm.AddBotCommand(new BotCommand { Name = parts[0], Response = parts[1] });
    }

    private void AddShout_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var response = NewShoutResponse.Text.Trim();
        if (_editingShout != null && string.IsNullOrWhiteSpace(response))
        {
            ResetShoutForm(clearEditing: true);
            ShoutFormError.Visibility = Visibility.Collapsed;
            return;
        }

        if (string.IsNullOrWhiteSpace(response))
        {
            ShoutFormError.Text = "Type a response first - the grey text in the box is only an example.";
            ShoutFormError.Visibility = Visibility.Visible;
            return;
        }

        ShoutFormError.Visibility = Visibility.Collapsed;
        var evtTag = (NewShoutEvent.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Follow";
        if (!Enum.TryParse<EventType>(evtTag, out var evtType)) evtType = EventType.Follow;
        if (_editingShout != null)
            _vm.RemoveBotShout(_editingShout);

        _vm.AddBotShout(new BotShout
        {
            EventFilter = evtType,
            Target = ParseReplyTarget(NewShoutTarget),
            Response = response,
        });
        ResetShoutForm(clearEditing: true);
    }

    private void RemoveShout_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || ShoutList.SelectedItem is not BotShout shout) return;
        if (ReferenceEquals(_editingShout, shout))
            ResetShoutForm(clearEditing: true);
        _vm.RemoveBotShout(shout);
    }

    private void EditShout_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not BotShout shout) return;
        _editingShout = shout;
        SetEventType(NewShoutEvent, shout.EventFilter);
        SetReplyTarget(NewShoutTarget, shout.Target);
        NewShoutResponse.Text = shout.Response;
        AddShoutBtn.Content = "Save";
        ShoutFormError.Visibility = Visibility.Collapsed;
    }

    private void DeleteShout_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || (sender as Button)?.Tag is not BotShout shout) return;
        if (ReferenceEquals(_editingShout, shout))
            ResetShoutForm(clearEditing: true);
        _vm.RemoveBotShout(shout);
    }

    private void AddShoutExample_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || (sender as Button)?.Tag is not string tag) return;
        var parts = tag.Split('|', 3);
        if (parts.Length != 3) return;
        if (!Enum.TryParse<EventType>(parts[0], out var evtType)) evtType = EventType.Follow;
        if (!Enum.TryParse<BotReplyTarget>(parts[1], out var target)) target = BotReplyTarget.Both;
        _vm.AddBotShout(new BotShout { EventFilter = evtType, Target = target, Response = parts[2] });
    }

    private void AddTimer_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var name = NewTimerName.Text.Trim();
        var message = NewTimerMessage.Text.Trim();
        if (_editingTimer != null && string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(message))
        {
            ResetTimerForm(clearEditing: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(message)) return;
        if (!int.TryParse(NewTimerInterval.Text, out var interval)) interval = 30;
        if (_editingTimer != null)
            _vm.RemoveBotTimer(_editingTimer);

        _vm.AddBotTimer(new BotTimer
        {
            Name = name,
            Message = message,
            IntervalMinutes = Math.Max(1, interval),
            Target = ParseReplyTarget(NewTimerTarget),
        });
        ResetTimerForm(clearEditing: true);
    }

    private void RemoveTimer_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || TimerList.SelectedItem is not BotTimer timer) return;
        if (ReferenceEquals(_editingTimer, timer))
            ResetTimerForm(clearEditing: true);
        _vm.RemoveBotTimer(timer);
    }

    private void EditTimer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not BotTimer timer) return;
        _editingTimer = timer;
        NewTimerName.Text = timer.Name;
        NewTimerInterval.Text = Math.Max(1, timer.IntervalMinutes).ToString();
        SetReplyTarget(NewTimerTarget, timer.Target);
        NewTimerMessage.Text = timer.Message;
        AddTimerBtn.Content = "Save";
    }

    private void DeleteTimer_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || (sender as Button)?.Tag is not BotTimer timer) return;
        if (ReferenceEquals(_editingTimer, timer))
            ResetTimerForm(clearEditing: true);
        _vm.RemoveBotTimer(timer);
    }

    private void SaveAutoMod_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        int.TryParse(TimeoutSecsBox.Text, out var secs);
        var words = (BlockedWordsBox.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _vm.SaveChatbotSettings(
            FilterLinksCheck.IsChecked == true,
            FilterCapsCheck.IsChecked == true,
            TimeoutCheck.IsChecked == true,
            Math.Max(1, secs),
            words);
    }

    private void ResetCommandForm(bool clearEditing)
    {
        NewCmdName.Text = "";
        NewCmdResponse.Text = "";
        NewCmdTarget.SelectedIndex = 0;
        NewCmdModOnly.IsChecked = false;
        NewCmdAllowedUsers.Text = "";
        NewCmdCooldown.Text = "";
        ClearCmdSound_Click(this, new RoutedEventArgs());
        NewCmdAlertEnabled.IsChecked = false;
        NewCmdAlertName.SelectedItem = null;
        if (clearEditing)
        {
            _editingCommand = null;
            AddCommandBtn.Content = "Add";
        }
    }

    private void ResetShoutForm(bool clearEditing)
    {
        NewShoutEvent.SelectedIndex = 0;
        NewShoutTarget.SelectedIndex = 0;
        NewShoutResponse.Text = "";
        if (clearEditing)
        {
            _editingShout = null;
            AddShoutBtn.Content = "Add";
        }
    }

    private void ResetTimerForm(bool clearEditing)
    {
        NewTimerName.Text = "";
        NewTimerInterval.Text = "30";
        NewTimerTarget.SelectedIndex = 0;
        NewTimerMessage.Text = "";
        if (clearEditing)
        {
            _editingTimer = null;
            AddTimerBtn.Content = "Add";
        }
    }

    private void SetCommandSound(string? soundFile)
    {
        if (string.IsNullOrWhiteSpace(soundFile))
        {
            ClearCmdSound_Click(this, new RoutedEventArgs());
            return;
        }

        _newCmdSoundPath = soundFile;
        NewCmdSoundLabel.Text = System.IO.Path.GetFileName(soundFile);
        ClearCmdSoundBtn.Visibility = Visibility.Visible;
    }

    private static BotReplyTarget ParseReplyTarget(ComboBox box)
    {
        var val = (box.SelectedItem as ComboBoxItem)?.Content?.ToString();
        return val switch
        {
            "Twitch" => BotReplyTarget.Twitch,
            "Kick" => BotReplyTarget.Kick,
            "YouTube" => BotReplyTarget.YouTube,
            _ => BotReplyTarget.Both,
        };
    }

    private static void SetReplyTarget(ComboBox box, BotReplyTarget target)
    {
        var desired = target switch
        {
            BotReplyTarget.Twitch => "Twitch",
            BotReplyTarget.Kick => "Kick",
            BotReplyTarget.YouTube => "YouTube",
            _ => "Both",
        };
        foreach (var item in box.Items)
            if (item is ComboBoxItem comboItem && string.Equals(comboItem.Content?.ToString(), desired, StringComparison.Ordinal))
            {
                box.SelectedItem = comboItem;
                return;
            }
        box.SelectedIndex = 0;
    }

    private static void SetEventType(ComboBox box, EventType eventType)
    {
        var desired = eventType.ToString();
        foreach (var item in box.Items)
            if (item is ComboBoxItem comboItem && string.Equals(comboItem.Tag?.ToString(), desired, StringComparison.Ordinal))
            {
                box.SelectedItem = comboItem;
                return;
            }
        box.SelectedIndex = 0;
    }
}
