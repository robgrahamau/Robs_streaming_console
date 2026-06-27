using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Steaming.WinUI;

public sealed partial class LoginWindow : Window
{
    private readonly string _redirectUri;
    private readonly bool   _isFragment;
    private readonly string? _profileName;

    private TaskCompletionSource<LoginResult> _tcs = new();

    public LoginWindow(string title, string url, string redirectUri, bool isFragment, string? profileName = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        _redirectUri   = redirectUri;
        _isFragment    = isFragment;
        _profileName   = profileName;

        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 700));

        _ = InitBrowserAsync(url);
    }

    private async Task InitBrowserAsync(string url)
    {
        await Browser.EnsureCoreWebView2Async();
        Browser.CoreWebView2.NavigationStarting += OnNavigating;
        Browser.Source = new Uri(url);
    }

    private void OnNavigating(Microsoft.Web.WebView2.Core.CoreWebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith(_redirectUri, StringComparison.OrdinalIgnoreCase))
            return;

        e.Cancel = true;
        var uri = new Uri(e.Uri);

        string? token = null, code = null;
        if (_isFragment)
        {
            var fragment = uri.Fragment.TrimStart('#');
            token = ParseParam(fragment, "access_token");
        }
        else
        {
            code = ParseParam(uri.Query.TrimStart('?'), "code");
        }

        _tcs.TrySetResult(new LoginResult(token, code));
        DispatcherQueue.TryEnqueue(Close);
    }

    public Task<LoginResult> WaitForResultAsync()
    {
        Closed += (_, _) => _tcs.TrySetResult(new LoginResult(null, null));
        return _tcs.Task;
    }

    // Domains whose WebView2 cookies keep a platform "logged in" inside the embedded browser. Clearing
    // the stored OAuth token is NOT enough on its own: if these cookies survive, the next login window
    // silently re-authenticates the same account with no prompt. Deleting the user-data FOLDER does not
    // work either — every WebView2 in the process shares one folder (WEBVIEW2_USER_DATA_FOLDER) that is
    // usually locked — so we delete the cookies through a live CookieManager instead.
    private static string[]? CookieHostsFor(string? platform) => platform?.ToLowerInvariant() switch
    {
        "twitch" => new[] { "https://twitch.tv", "https://www.twitch.tv", "https://id.twitch.tv", "https://passport.twitch.tv" },
        "kick"   => new[] { "https://kick.com", "https://www.kick.com", "https://id.kick.com" },
        "youtube" => new[] { "https://youtube.com", "https://www.youtube.com", "https://accounts.google.com" },
        _        => null,
    };

    // Best-effort browser logout: clears the platform's session cookies so the next connect shows a real
    // login prompt. The stored token is cleared separately and is the authoritative logout; this just
    // stops silent re-auth. Runs a throwaway 0-size WebView2 on the supplied hidden host panel.
    public static async Task ClearPlatformCookiesAsync(Panel? host, string? platform)
    {
        var hosts = CookieHostsFor(platform);
        if (host == null || hosts == null) return;

        WebView2? wv = null;
        try
        {
            wv = new WebView2 { Width = 0, Height = 0 };
            host.Children.Add(wv);              // live visual tree so CoreWebView2 initialises
            await wv.EnsureCoreWebView2Async();
            var mgr = wv.CoreWebView2.CookieManager;
            foreach (var h in hosts)
            {
                var cookies = await mgr.GetCookiesAsync(h);
                foreach (var c in cookies) mgr.DeleteCookie(c);
            }
        }
        catch { /* best-effort; the token clear already logged the app out */ }
        finally
        {
            if (wv != null)
            {
                try { wv.Close(); } catch { }
                try { host.Children.Remove(wv); } catch { }
            }
        }
    }

    private static string? ParseParam(string query, string key)
    {
        foreach (var part in query.Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key)
                return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }
}

public sealed record LoginResult(string? Token, string? Code);
