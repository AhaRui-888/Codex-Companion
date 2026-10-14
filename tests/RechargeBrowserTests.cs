using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// Opt-in integration suite: requires the installed Evergreen Runtime. Every
// browser request is intercepted; no real account, order, or payment is used.
internal static class RechargeBrowserTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string root)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        WebViewRuntime.InstallAssemblyResolver();
        RunWithSdk(root, false);
        RunWithSdk(root, true);
        Console.WriteLine("Recharge browser: private session seeding, HttpOnly cookies, rotation/logout during close, handoff and profile cleanup passed.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunWithSdk(string root, bool logout)
    {
        Console.WriteLine("Recharge browser starting: " + (logout ? "logout" : "rotation"));
        string data = Path.Combine(root, logout ? "logout" : "rotation");
        var store = new AccountSessionStore(data);
        var original = new AccountSession { AccessToken = "fixture-browser-access", RefreshToken = "fixture-browser-refresh" };
        store.Save(original);
        AccountSession returned = original.Copy();
        int callbacks = 0;
        Exception failure = null;
        bool requestStarted = false, closeRequested = false;
        var streams = new List<Stream>();
        Type dialogType = typeof(OfficialLogin).GetNestedType("BrowserLoginForm", BindingFlags.NonPublic);
        Action<AccountSession> changed = value => {
            returned = value == null ? null : value.Copy();
            callbacks++;
            if (value == null) store.Clear(); else store.Save(value);
        };
        using (var dialog = (Form)Activator.CreateInstance(dialogType, Members, null,
            new object[] { data, null, original, changed }, null))
        using (var watchdog = new System.Windows.Forms.Timer { Interval = 30000 })
        {
            dialog.Opacity = 0;
            var browser = (WebView2)dialogType.GetField("browser", Members).GetValue(dialog);
            browser.CoreWebView2InitializationCompleted += delegate(object sender, CoreWebView2InitializationCompletedEventArgs args) {
                Console.WriteLine("Browser initialized: " + args.IsSuccess);
                if (!args.IsSuccess) return;
                var core = browser.CoreWebView2;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += async delegate(object requestSender, CoreWebView2WebResourceRequestedEventArgs request) {
                    using (var deferral = request.GetDeferral())
                    {
                        string body = "", headers = "Content-Type: text/html; charset=utf-8\r\n";
                        int code = 200;
                        try
                        {
                            Assert(OfficialLogin.IsOfficialOrigin(request.Request.Uri), "No request may leave the official origin.");
                            string path = new Uri(request.Request.Uri).AbsolutePath;
                            Console.WriteLine("Intercepted fixture request: " + path);
                            if (path == "/") body = "<!doctype html><html><title>Isolated recharge fixture</title><body>Recharge fixture</body></html>";
                            else if (path == "/api/auth/refresh" || path == "/api/auth/logout")
                            {
                                string cookies = request.Request.Headers.GetHeader("Cookie");
                                Assert(cookies.Contains("codex_access=fixture-browser-access") && cookies.Contains("codex_refresh=fixture-browser-refresh"),
                                    "The embedded website must receive the verified session.");
                                requestStarted = true;
                                // Keep the website request pending while the user closes the dialog.
                                await Task.Delay(350);
                                headers = "Content-Type: application/json\r\n";
                                headers += logout
                                    ? "Set-Cookie: codex_access=; Path=/; Secure; HttpOnly; SameSite=Strict; Max-Age=0\r\nSet-Cookie: codex_refresh=; Path=/; Secure; HttpOnly; SameSite=Strict; Max-Age=0\r\n"
                                    : "Set-Cookie: codex_access=fixture-browser-new-access; Path=/; Secure; HttpOnly; SameSite=Strict\r\nSet-Cookie: codex_refresh=fixture-browser-new-refresh; Path=/; Secure; HttpOnly; SameSite=Strict\r\n";
                                body = "{\"authenticated\":" + (logout ? "false" : "true") + "}";
                            }
                            else code = 204;
                        }
                        catch (Exception error) { failure = error; code = 500; }
                        var content = new MemoryStream(Encoding.UTF8.GetBytes(body));
                        streams.Add(content);
                        request.Response = core.Environment.CreateWebResourceResponse(content, code, code == 200 ? "OK" : "Fixture", headers);
                    }
                };
            };
            watchdog.Tick += delegate {
                Console.WriteLine("Browser watchdog expired: initialized=" + dialogType.GetField("initialized", Members).GetValue(dialog)
                    + ", auth=" + dialogType.GetField("authenticationRequests", Members).GetValue(dialog)
                    + ", closeRequested=" + dialogType.GetField("rechargeCloseRequested", Members).GetValue(dialog)
                    + ", closeReady=" + dialogType.GetField("rechargeCloseReady", Members).GetValue(dialog)
                    + ", capture=" + dialogType.GetField("capturingRechargeSession", Members).GetValue(dialog));
                failure = new TimeoutException("Recharge browser did not complete within 30 seconds.");
                dialogType.GetField("rechargeCloseReady", Members).SetValue(dialog, true);
                dialog.Close();
            };
            dialog.Shown += async delegate {
                Console.WriteLine("Browser dialog shown.");
                try
                {
                    await UntilAsync(() => browser.CoreWebView2 != null && (bool)dialogType.GetField("documentLoaded", Members).GetValue(dialog));
                    var core = browser.CoreWebView2;
                    Console.WriteLine("Browser fixture loaded.");
                    Assert(core.Source == OfficialLogin.LoginUrl && core.Profile.IsInPrivateModeEnabled,
                        "Recharge must remain open at the official recharge route in a private profile.");
                    Assert(await core.ExecuteScriptAsync("document.cookie") == "\"\"", "Session credentials must remain inaccessible to page scripts.");
                    string route = logout ? "logout" : "refresh";
                    await core.ExecuteScriptAsync("fetch('/api/auth/" + route + "',{method:'POST',credentials:'include'})");
                    await UntilAsync(() => requestStarted || failure != null);
                    if (failure != null) throw failure;
                    Assert((int)dialogType.GetField("authenticationRequests", Members).GetValue(dialog) > 0,
                        "The recharge dialog must track the website's pending authentication mutation.");
                    closeRequested = true;
                    Console.WriteLine("Closing while fixture authentication is pending.");
                    dialog.Close();
                    Assert(dialog.Visible, "Closing must wait for the pending website session handoff.");
                }
                catch (Exception error)
                {
                    failure = error;
                    dialogType.GetField("rechargeCloseReady", Members).SetValue(dialog, true);
                    dialog.Close();
                }
            };
            watchdog.Start();
            dialog.ShowDialog();
            Console.WriteLine("Browser dialog returned.");
            watchdog.Stop();
        }
        foreach (Stream stream in streams) stream.Dispose();
        if (failure != null) throw new InvalidOperationException("Recharge browser " + (logout ? "logout" : "rotation") + " failed.", failure);
        Assert(closeRequested && callbacks > 0, "Closing must synchronize the website session.");
        if (logout) Assert(returned == null && store.Load() == null, "Website logout must remove the assistant's stale credentials.");
        else Assert(returned != null && returned.AccessToken == "fixture-browser-new-access" && returned.RefreshToken == "fixture-browser-new-refresh"
            && store.Load().RefreshToken == returned.RefreshToken, "The rotated session must be saved before closing.");
        string profiles = Path.Combine(data, "login-profiles");
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (Directory.Exists(profiles) && Directory.GetDirectories(profiles).Length > 0 && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(100);
        }
        Assert(!Directory.Exists(profiles) || Directory.GetDirectories(profiles).Length == 0, "Closing must clean up the temporary browser profile.");
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The isolated browser did not reach its expected state.");
            await Task.Delay(25);
        }
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
