using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Steaming.Core.Platforms;
using Steaming.Core.Services;

namespace Steaming.WinUI.Services;

// Resolves a Kick channel's real chatroom_id by doing the lookup through a hidden WebView2 (real
// Chromium TLS + the Cloudflare clearance cookie from the user's Kick login) instead of HttpClient.
// kick.com/api/v2/channels/{slug} is Cloudflare-protected and returns 403 to plain HTTP clients;
// a real browser engine passes. Used only when Kick raid alerts are enabled (opt-in).
public sealed class KickWebChannelResolver
{
    private readonly DispatcherQueue _ui;
    private readonly Panel _host;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebView2? _wv;

    public KickWebChannelResolver(DispatcherQueue ui, Panel host)
    {
        _ui = ui;
        _host = host;
    }

    public async Task<KickChannelLookupResult> ResolveAsync(string slug, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false)) return default;
        try
        {
            var tcs = new TaskCompletionSource<KickChannelLookupResult>();
            if (!_ui.TryEnqueue(async () =>
            {
                try { tcs.TrySetResult(await ResolveOnUiAsync(slug, ct)); }
                catch (Exception ex)
                {
                    DebugLogFile.Append($"[KickRaid] WebView2 resolver error: {ex.Message}");
                    tcs.TrySetResult(default);
                }
            }))
            {
                return default; // UI queue gone (shutting down)
            }
            return await tcs.Task.ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<KickChannelLookupResult> ResolveOnUiAsync(string slug, CancellationToken ct)
    {
      try
      {
        await EnsureWebViewAsync().ConfigureAwait(true);
        var core = _wv!.CoreWebView2;

        // Navigate to the API endpoint itself (NOT the kick.com homepage — that autoplays the featured
        // live stream's audio/video). The API URL is plain JSON: no media, still on the kick.com origin,
        // and it gets the same Cloudflare clearance (Chromium auto-solves any JS challenge, then lands on
        // the JSON). Being on-origin also lets the same-origin fetch below send cf_clearance cookies.
        var src = core.Source ?? "";
        if (!src.StartsWith("https://kick.com", StringComparison.OrdinalIgnoreCase))
        {
            var navDone = new TaskCompletionSource<bool>();
            void OnNav(CoreWebView2 s, CoreWebView2NavigationCompletedEventArgs e) => navDone.TrySetResult(true);
            core.NavigationCompleted += OnNav;
            try
            {
                core.Navigate($"https://kick.com/api/v2/channels/{Uri.EscapeDataString(slug)}");
                await Task.WhenAny(navDone.Task, Task.Delay(15000, ct)).ConfigureAwait(true);
            }
            finally { core.NavigationCompleted -= OnNav; }
            await Task.Delay(2000, ct).ConfigureAwait(true); // let a Cloudflare challenge settle
        }

        var slugLit = JsonSerializer.Serialize(slug); // safely-escaped JS string literal

        // Up to 3 fetch attempts (a pre-clearance attempt can return the Cloudflare HTML once).
        for (int attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
        {
            var kickoff =
                "window.__kc=undefined;" +
                "fetch('/api/v2/channels/'+" + slugLit + ",{headers:{'Accept':'application/json'}})" +
                ".then(r=>r.text()).then(t=>{window.__kc=t;}).catch(e=>{window.__kc='__ERR__';});0;";
            try { await core.ExecuteScriptAsync(kickoff); }
            catch (Exception ex) { DebugLogFile.Append($"[KickRaid] fetch kickoff failed: {ex.Message}"); }

            // Poll for the fetch result.
            for (int i = 0; i < 12 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(600, ct).ConfigureAwait(true);
                string raw;
                try { raw = await core.ExecuteScriptAsync("window.__kc===undefined?null:window.__kc"); }
                catch (Exception ex) { DebugLogFile.Append($"[KickRaid] poll failed: {ex.Message}"); break; }

                if (string.IsNullOrEmpty(raw) || raw == "null") continue; // not back yet
                var text = JsonSerializer.Deserialize<string>(raw) ?? "";
                if (text == "__ERR__") { DebugLogFile.Append("[KickRaid] fetch errored."); break; }

                var info = TryParseChannelInfo(text);
                if (info.ChatroomId > 0) return info;
                break; // got a non-JSON body (Cloudflare HTML) — retry the fetch
            }
            await Task.Delay(2000, ct).ConfigureAwait(true);
        }

        DebugLogFile.Append("[KickRaid] WebView2 lookup yielded no chatroom id (Cloudflare challenge / not logged in?).");
        return default;
      }
      finally
      {
          // Throwaway: the browser is needed ONLY for this one id lookup, so tear it down immediately.
          // There is never a persistent WebView2 (or any media/network) left parked in the app.
          DisposeWebView();
      }
    }

    private void DisposeWebView()
    {
        if (_wv == null) return;
        try { _wv.CoreWebView2?.Stop(); } catch { }
        try { _wv.Close(); } catch { }                 // closes CoreWebView2, stops all media + network
        try { _host.Children.Remove(_wv); } catch { }
        _wv = null;
    }

    private static KickChannelLookupResult TryParseChannelInfo(string text)
    {
        var t = (text ?? "").TrimStart();
        if (!t.StartsWith("{")) return default;
        try
        {
            using var doc = JsonDocument.Parse(t);
            int chatroomId = 0;
            int? followersCount = null;
            if (doc.RootElement.TryGetProperty("chatroom", out var c) &&
                c.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id) && id > 0)
                chatroomId = id;
            if (doc.RootElement.TryGetProperty("followers_count", out var fcEl) && fcEl.TryGetInt32(out var fc) && fc >= 0)
                followersCount = fc;
            else if (doc.RootElement.TryGetProperty("followersCount", out var fcEl2) && fcEl2.TryGetInt32(out var fc2) && fc2 >= 0)
                followersCount = fc2;
            return new KickChannelLookupResult(chatroomId, followersCount);
        }
        catch { }
        return default;
    }

    private async Task EnsureWebViewAsync()
    {
        if (_wv?.CoreWebView2 != null) return;
        _wv = new WebView2 { Width = 0, Height = 0 };
        _host.Children.Add(_wv);                 // live visual tree so CoreWebView2 initialises
        await _wv.EnsureCoreWebView2Async();
        var core = _wv.CoreWebView2;
        core.IsMuted = true;
        core.NavigationCompleted += OnNavigationCompleted;
    }

    private async void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            sender.IsMuted = true;
            await sender.ExecuteScriptAsync(@"
                (() => {
                    const silence = media => {
                        try {
                            media.muted = true;
                            media.volume = 0;
                            media.pause();
                            const stop = () => {
                                try { media.muted = true; media.volume = 0; media.pause(); } catch {}
                            };
                            media.addEventListener('play', stop);
                            media.addEventListener('playing', stop);
                        } catch {}
                    };
                    document.querySelectorAll('video,audio').forEach(silence);
                    const obs = new MutationObserver(() => {
                        document.querySelectorAll('video,audio').forEach(silence);
                    });
                    obs.observe(document.documentElement || document.body, { childList: true, subtree: true });
                })();
            ");
        }
        catch (Exception ex)
        {
            DebugLogFile.Append($"[KickRaid] media-silence hook failed: {ex.Message}");
        }
    }
}
