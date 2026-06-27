using System.Diagnostics;
using System.Net;
using System.Text;

namespace Steaming.Core.Auth;

public class OAuthService
{
    // Opens the browser to authUrl, starts a local listener, returns the token or null on timeout/cancel.
    // For Twitch implicit flow the token is in the URL fragment — we serve a small HTML page
    // that posts it back to us. For Kick auth code flow the code arrives as a query param.
    public async Task<OAuthResult?> AuthorizeAsync(
        string authUrl,
        int    port,
        bool   fragmentFlow,   // true = Twitch implicit (token in fragment), false = code flow
        int    timeoutSeconds = 120)
    {
        var prefix = $"http://localhost:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            if (fragmentFlow)
            {
                // Step 1: browser requests /callback — serve JS page that reads fragment and POSTs token
                // Step 2: JS POSTs to /token — we read it and return
                string? token = null;

                while (token == null && !cts.IsCancellationRequested)
                {
                    var ctx = await listener.GetContextAsync().WaitAsync(cts.Token);
                    var path = ctx.Request.Url?.AbsolutePath ?? "";

                    if (path == "/callback" || path == "/")
                    {
                        var html = """
                            <!DOCTYPE html><html><body style="font-family:Segoe UI;background:#202020;color:white;padding:40px">
                            <h2>Streaming — Logging in...</h2>
                            <script>
                              var hash = window.location.hash.substring(1);
                              var params = new URLSearchParams(hash);
                              var token = params.get('access_token');
                              if (token) {
                                fetch('/token?value=' + encodeURIComponent(token))
                                  .then(function() {
                                    document.body.innerHTML = '<h2>✓ Login successful — you can close this tab.</h2>';
                                  });
                              } else {
                                document.body.innerHTML = '<h2>✗ No token found. Please try again.</h2>';
                              }
                            </script>
                            </body></html>
                            """;
                        var bytes = Encoding.UTF8.GetBytes(html);
                        ctx.Response.ContentType = "text/html";
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes, cts.Token);
                        ctx.Response.Close();
                    }
                    else if (path == "/token")
                    {
                        token = ctx.Request.QueryString["value"];
                        var bytes = Encoding.UTF8.GetBytes("OK");
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes, cts.Token);
                        ctx.Response.Close();
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                    }
                }

                return token != null ? new OAuthResult(token, null) : null;
            }
            else
            {
                // Auth code flow — code arrives as ?code= query param
                var ctx = await listener.GetContextAsync().WaitAsync(cts.Token);
                var code = ctx.Request.QueryString["code"];
                var error = ctx.Request.QueryString["error"];

                var html = code != null
                    ? """<html><body style="font-family:Segoe UI;background:#202020;color:white;padding:40px"><h2>✓ Login successful — you can close this tab.</h2></body></html>"""
                    : $"""<html><body style="font-family:Segoe UI;background:#202020;color:white;padding:40px"><h2>✗ Login failed: {error}</h2></body></html>""";

                var bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();

                return code != null ? new OAuthResult(null, code) : null;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            listener.Stop();
        }
    }

    public static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

public record OAuthResult(string? AccessToken, string? Code);
