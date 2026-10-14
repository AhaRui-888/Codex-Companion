using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

internal static class AccountTests
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        TestWalletParsing();
        TestTransport();
        TestLoginBoundary();
        TestSessionStore(Path.Combine(root, "encrypted-session"));
        TestRequestsAndRefresh(Path.Combine(root, "rotated-session"));
        TestClosingDuringRefresh(Path.Combine(root, "closing-session"));
        TestRechargeFlow(Path.Combine(root, "recharge-session"));
        var previousContext = SynchronizationContext.Current;
        try { TestApiKeys(Path.Combine(root, "account-keys")); }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        TestRejectedResponses();
        TestSerializedRefresh();
        Console.WriteLine("Account: official cookie authentication, CNY balance units, refresh rotation, expiry, encrypted persistence and logout passed.");
    }

    private static void TestWalletParsing()
    {
        AccountSnapshot result = AccountClient.ParseSnapshot(AccountClient.ParseObject("{\"id\":\"fixture-user\",\"email\":\"fixture@example.test\"}"),
            AccountClient.ParseObject("{\"balanceMicroCny\":\"123456789\",\"reservedMicroCny\":\"2345678\"}"));
        Assert(result.Balance == 123.456789M && result.Reserved == 2.345678M && result.AvailableBalance == 121.111111M, "micro-CNY must retain decimal precision and subtract reserved funds");
        Assert(result.AccountName == "fixture@example.test", "deployed email users must load");
        result = AccountClient.ParseSnapshot(AccountClient.ParseObject("{\"id\":\"fixture-phone\",\"phone\":\"13800000000\"}"),
            AccountClient.ParseObject("{\"balanceMicroCny\":0,\"reservedMicroCny\":0}"));
        Assert(result.AccountName == "13800000000" && result.AvailableBalance == 0, "phone users and zero balances must load");
        result = AccountClient.ParseSnapshot(AccountClient.ParseObject("{\"id\":\"fixture-user\",\"email\":\"fixture@example.test\"}"),
            AccountClient.ParseObject("{\"balanceMicroCny\":\"-1000000\",\"reservedMicroCny\":\"2000000\"}"));
        Assert(result.AvailableBalance == -3M, "signed wallet values must match the website instead of being silently clamped");
        foreach (string invalid in new[] { "{}", "{\"balanceMicroCny\":true,\"reservedMicroCny\":0}", "{\"balanceMicroCny\":\"1.5\",\"reservedMicroCny\":0}", "{\"balanceMicroCny\":\"9223372036854775808\",\"reservedMicroCny\":0}" })
            Reject<InvalidDataException>(() => AccountClient.ParseSnapshot(AccountClient.ParseObject("{\"id\":\"fixture-user\",\"email\":\"fixture@example.test\"}"), AccountClient.ParseObject(invalid)));
        Assert(AccountPanel.MaskAccount("fixture@example.test") == "fi***@example.test", "emails must be masked in the account bar");
        Assert(AccountPanel.MaskAccount("13800000000") == "138****0000", "phone accounts must be masked");
    }

    private const string KeyFixture = "[{\"id\":\"one\",\"name\":\"工作\",\"keyPrefix\":\"sk-fixture-one-\",\"status\":\"ACTIVE\",\"expiresAt\":null},{\"id\":\"two\",\"name\":\"个人\",\"keyPrefix\":\"sk-fixture-two-\",\"status\":\"ACTIVE\"},{\"id\":\"off\",\"name\":\"已停用\",\"keyPrefix\":\"sk-fixture-off-\",\"status\":\"DISABLED\"}]";

    private static void TestApiKeys(string root)
    {
        var entries = AccountClient.ParseApiKeys(KeyFixture);
        Assert(entries.Length == 3 && entries[0].Usable && !entries[2].Usable, "key metadata must preserve multiple keys and disabled state");
        foreach (string invalid in new[] { "{}", "[{}]", KeyFixture.Replace("\"two\"", "\"one\""), KeyFixture.Replace("null", "\"bad-date\"") })
            Reject<InvalidDataException>(() => AccountClient.ParseApiKeys(invalid));
        var handler = new AccountHandler(true);
        var client = new AccountClient(cookies => { handler.Cookies = cookies; return handler; }, TimeSpan.FromSeconds(3));
        var session = Session();
        string savedRefresh = null;
        var keys = client.FetchApiKeysAsync(session, CancellationToken.None, updated => savedRefresh = updated.RefreshToken).GetAwaiter().GetResult();
        Assert(keys.Length == 3 && handler.Refreshes == 1 && savedRefresh == "rotated-refresh" && handler.SafeRequests,
            "automatic key list loading must preserve HTTPS authentication and single-use session rotation");
        using (var picker = new AccountKeyPicker(root))
        {
            picker.SetAccount("user-one", entries, null);
            Assert(picker.AvailableKey(null) == null, "prefix-only metadata must never be filled as a credential");
            picker.Remember("sk-fixture-one-secret");
            picker.Remember("sk-fixture-two-secret");
            Assert(picker.AvailableKey(null) == "sk-fixture-one-secret", "a previously saved complete key can autofill");
            Assert(picker.AvailableKey("sk-fixture-two-secret") == "sk-fixture-two-secret",
                "the current configured key must take priority over another cached account key");
            Assert(picker.ContainsUsableKey("sk-fixture-two-secret") && !picker.ContainsUsableKey("sk-independent-manual-fixture"),
                "an independently typed credential must not be classified as an automatically filled account key");
            var combo = (System.Windows.Forms.ListBox)typeof(AccountKeyPicker).GetField("keys", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(picker);
            string selected = null;
            picker.KeyChosen += value => selected = value;
            combo.SelectedIndex = 1;
            typeof(AccountKeyPicker).GetMethod("SelectKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(picker, null);
            Assert(selected == "sk-fixture-two-secret", "selecting another cached key must use that key");
            foreach (string path in Directory.GetFiles(Path.Combine(root, "api-key-cache")))
                Assert(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("sk-fixture"), "cached keys must use DPAPI encryption");
            picker.SetAccount("user-two", entries, null);
            Assert(picker.AvailableKey(null) == null, "account changes must not reuse another user's cached keys");
            picker.SetAccount("user-one", AccountClient.ParseApiKeys(KeyFixture.Replace("ACTIVE", "REVOKED")), null);
            Assert(picker.AvailableKey(null) == null, "revoked keys must not autofill from the cache");
            picker.SetAccount(null, new AccountApiKey[0], null);
            Assert(!picker.HasAccount && picker.LogicalHeight == 0 && picker.AvailableKey(null) == null, "logout must clear the list and disable key selection");
        }
    }

    private static void TestTransport()
    {
        using (var handler = AccountClient.CreateHandler(new CookieContainer()))
        {
            Assert(handler.SslProtocols == SslProtocols.Tls12 && !handler.AllowAutoRedirect && handler.UseCookies && !handler.UseDefaultCredentials,
                "the website account transport must retain certificate validation and reject redirects");
            Assert(handler.ServerCertificateCustomValidationCallback == null, "TLS certificate checks must stay enabled");
        }
        foreach (string token in new[] { "", "a;other=secret", "a\r\nInjected: value", "a value", "é", new string('a', 8193) })
            Reject<InvalidDataException>(() => AccountSession.ValidateToken(token));
    }

    private static void TestLoginBoundary()
    {
        Assert(OfficialLogin.IsOfficialOrigin("https://xai-tools.cn/#recharge") && OfficialLogin.IsOfficialOrigin("https://xai-tools.cn:443/api/auth/me"), "official HTTPS login origin");
        foreach (string origin in new[] { "http://xai-tools.cn/", "https://xai-tools.cn.evil.test/", "https://login.xai-tools.cn/", "https://xai-tools.cn:8443/", "https://user@xai-tools.cn/", "file:///login.html", "javascript:alert(1)" })
            Assert(!OfficialLogin.IsOfficialOrigin(origin), "untrusted login origins must not supply cookies");
        Assert(OfficialLogin.IsOfficialCookie("xai-tools.cn", "/", true, true) && OfficialLogin.IsOfficialCookie(".xai-tools.cn", "/", true, true), "official secure HttpOnly cookies");
        Assert(!OfficialLogin.IsOfficialCookie("login.xai-tools.cn", "/", true, true) && !OfficialLogin.IsOfficialCookie("xai-tools.cn", "/api", true, true) &&
            !OfficialLogin.IsOfficialCookie("xai-tools.cn", "/", false, true) && !OfficialLogin.IsOfficialCookie("xai-tools.cn", "/", true, false), "mismatched cookie boundaries must be rejected");
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        foreach (string resource in new[] { "WebView2.Microsoft.Web.WebView2.Core.dll", "WebView2.Microsoft.Web.WebView2.WinForms.dll", "WebView2.Loader.x86", "WebView2.Loader.x64" })
            using (var stream = assembly.GetManifestResourceStream(resource))
                Assert(stream != null && stream.Length > 10000, "EXE-only updates must carry the complete pinned login SDK");
    }

    private static void TestSessionStore(string root)
    {
        var store = new AccountSessionStore(root);
        Assert(store.Load() == null && !Directory.Exists(root), "reading signed-out state must not create or touch other settings");
        var session = Session();
        store.Save(session);
        var restored = store.Load();
        Assert(restored.AccessToken == session.AccessToken && restored.RefreshToken == session.RefreshToken, "DPAPI session roundtrip");
        string encryptedText = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(store.SessionPath));
        Assert(!encryptedText.Contains(session.AccessToken) && !encryptedText.Contains(session.RefreshToken), "website cookies must not be saved as plaintext");
        store.Clear();
        Assert(store.Load() == null, "local logout must remove saved credentials");
        Directory.CreateDirectory(root);
        File.WriteAllText(store.SessionPath, "not-a-DPAPI-session");
        Reject<InvalidDataException>(() => store.Load());
    }

    private static void TestRequestsAndRefresh(string root)
    {
        var store = new AccountSessionStore(root);
        var session = Session();
        var handler = new AccountHandler(true);
        var client = new AccountClient(cookies => { handler.Cookies = cookies; return handler; }, TimeSpan.FromSeconds(3));
        AccountFetchResult result = client.FetchAsync(session, CancellationToken.None, store.Save).GetAwaiter().GetResult();
        Assert(result.Snapshot.AvailableBalance == 8M, "wallet must follow verified user identity");
        Assert(result.Session.AccessToken == "rotated-access" && result.Session.RefreshToken == "rotated-refresh", "both rotated cookies must return to the caller");
        Assert(store.Load().RefreshToken == "rotated-refresh" && session.RefreshToken == "rotated-refresh", "rotated refresh cookies must persist and replace the in-memory session");
        Assert(handler.Refreshes == 1 && handler.AuthenticatedRequests == 3, "an expired access cookie must refresh only once then retry");
        Assert(handler.SafeRequests, "requests must use only the exact official HTTPS origin without API Keys or bearer auth");

        var logoutHandler = new AccountHandler(false);
        client = new AccountClient(cookies => { logoutHandler.Cookies = cookies; return logoutHandler; }, TimeSpan.FromSeconds(3));
        client.LogoutAsync(result.Session, CancellationToken.None).GetAwaiter().GetResult();
        Assert(logoutHandler.Logouts == 1 && logoutHandler.Csrf, "server logout must revoke the website session with CSRF protection");

        var expiredLogoutHandler = new AccountHandler(false) { ExpireLogout = true };
        client = new AccountClient(cookies => { expiredLogoutHandler.Cookies = cookies; return expiredLogoutHandler; }, TimeSpan.FromSeconds(3));
        client.LogoutAsync(Session(), CancellationToken.None).GetAwaiter().GetResult();
        Assert(expiredLogoutHandler.Logouts == 2 && expiredLogoutHandler.Refreshes == 1,
            "an expired access cookie must be refreshed before server logout can be reported as successful");

        var failedWallet = new AccountHandler(true) { FailWallet = true };
        client = new AccountClient(cookies => { failedWallet.Cookies = cookies; return failedWallet; }, TimeSpan.FromSeconds(3));
        Reject<HttpRequestException>(() => client.FetchAsync(Session(), CancellationToken.None, store.Save).GetAwaiter().GetResult());
        Assert(store.Load().RefreshToken == "rotated-refresh", "a wallet failure must not lose the already-rotated refresh cookie");
    }

    private static void TestClosingDuringRefresh(string root)
    {
        Exception failure = null;
        var thread = new Thread(delegate()
        {
            try
            {
                var store = new AccountSessionStore(root);
                store.Save(Session());
                var handler = new AccountHandler(true);
                var client = new AccountClient(cookies => { handler.Cookies = cookies; return handler; }, TimeSpan.FromSeconds(3));
                using (var panel = new AccountPanel(false, client, store))
                using (var form = new System.Windows.Forms.Form { ShowInTaskbar = false, Opacity = 0 })
                {
                    form.Controls.Add(panel);
                    // The server has committed its replacement cookies. Close the panel
                    // before the successful response resumes the persistence callback.
                    handler.AfterRefreshResponse = delegate { form.Invoke((Action)panel.Dispose); };
                    form.Shown += async delegate
                    {
                        try
                        {
                            await panel.StartAsync();
                            var restored = store.Load();
                            Assert(panel.IsDisposed && handler.Refreshes == 1, "the refresh must overlap account-panel shutdown");
                            Assert(restored.AccessToken == "rotated-access" && restored.RefreshToken == "rotated-refresh",
                                "closing after server-side refresh rotation must persist both replacement cookies");
                            string encrypted = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(store.SessionPath));
                            Assert(!encrypted.Contains("rotated-access") && !encrypted.Contains("rotated-refresh"),
                                "cookies persisted during shutdown must remain DPAPI encrypted");
                        }
                        catch (Exception error) { failure = error; }
                        finally { form.Close(); }
                    };
                    // Keep this message loop and its WinForms synchronization context
                    // off the test-runner thread so later async account tests cannot hang.
                    System.Windows.Forms.Application.Run(form);
                }
            }
            catch (Exception error) { failure = error; }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert(thread.Join(TimeSpan.FromSeconds(10)), "the isolated shutdown regression must complete promptly");
        if (failure != null) throw new InvalidOperationException("Account shutdown regression failed.", failure);
    }

    private static void TestRechargeFlow(string root)
    {
        Exception failure = null;
        var thread = new Thread(delegate()
        {
            try
            {
                var store = new AccountSessionStore(root);
                var handler = new AccountHandler(false);
                var client = new AccountClient(cookies => { handler.Cookies = cookies; return handler; }, TimeSpan.FromSeconds(3));
                int opened = 0;
                AccountPanel panel = null;
                using (var form = new System.Windows.Forms.Form { ShowInTaskbar = false, Opacity = 0 })
                using (panel = new AccountPanel(false, client, store, (owner, directory, session, changed) => {
                    opened++;
                    Assert(session.AccessToken == "fixture-access", "recharge must receive the current authenticated session");
                    int requests = handler.AuthenticatedRequests;
                    panel.RefreshAsync(false).GetAwaiter().GetResult();
                    panel.RechargeAsync().GetAwaiter().GetResult();
                    panel.LogoutAsync().GetAwaiter().GetResult();
                    Assert(handler.AuthenticatedRequests == requests && opened == 1 && handler.Logouts == 0,
                        "the recharge window must exclusively own session refresh and prevent duplicate windows");
                    handler.ExpectedAccess = "recharge-access";
                    handler.WalletMicroCny = "50000000";
                    changed(new AccountSession { AccessToken = "recharge-access", RefreshToken = "recharge-refresh" });
                    Assert(store.Load().RefreshToken == "recharge-refresh", "website cookie rotation must persist before closing");
                }))
                {
                    form.Controls.Add(panel);
                    int keyLists = 0;
                    panel.ApiKeysChanged += (id, values, error) => {
                        if (id != null) { Assert(error == null && values.Length == 3, "login must automatically load this user's key list"); keyLists++; }
                    };
                    form.Shown += async delegate
                    {
                        try
                        {
                            var recharge = (System.Windows.Forms.Button)typeof(AccountPanel).GetField("rechargeButton",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(panel);
                            Assert(!recharge.Enabled, "signed-out accounts cannot recharge");
                            await panel.RechargeAsync();
                            Assert(opened == 0 && handler.AuthenticatedRequests == 0, "signed-out recharge must do nothing");
                            store.Save(Session());
                            await panel.StartAsync();
                            Assert(recharge.Enabled && keyLists == 1, "recharge and automatic key loading must follow successful login");
                            panel.SetOperationsEnabled(false);
                            await panel.RechargeAsync();
                            Assert(!recharge.Enabled && opened == 0, "configuration busy state must block recharge");
                            panel.SetOperationsEnabled(true);
                            int requests = handler.AuthenticatedRequests;
                            await panel.RechargeAsync();
                            Assert(opened == 1 && handler.AuthenticatedRequests == requests + 5,
                                "recharge must validate before opening and fetch the wallet again after closing");
                            var balance = (System.Windows.Forms.Label)typeof(AccountPanel).GetField("balanceLabel",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(panel);
                            Assert(balance.Text.Contains("48.00") && recharge.Enabled,
                                "closing recharge must display the new available balance and restore actions");
                            handler.RejectAuthentication = true;
                            handler.Revoked = true;
                            await panel.RechargeAsync();
                            Assert(opened == 1 && !recharge.Enabled && store.Load() == null,
                                "expired sessions must return to login instead of opening recharge");
                        }
                        catch (Exception error) { failure = error; }
                        finally { form.Close(); }
                    };
                    System.Windows.Forms.Application.Run(form);
                }
            }
            catch (Exception error) { failure = error; }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert(thread.Join(TimeSpan.FromSeconds(15)), "the recharge workflow regression must complete promptly");
        if (failure != null) throw new InvalidOperationException("Recharge workflow failed.", failure);
    }

    private static void TestRejectedResponses()
    {
        foreach (bool revoked in new[] { true, false })
        {
            var handler = new AccountHandler(false) { RejectAuthentication = true, Revoked = revoked };
            var client = new AccountClient(cookies => { handler.Cookies = cookies; return handler; }, TimeSpan.FromSeconds(3));
            Reject<AccountAuthenticationException>(() => client.FetchAsync(Session(), CancellationToken.None).GetAwaiter().GetResult());
            Assert(handler.Refreshes == (revoked ? 0 : 1), "revoked sessions must not refresh; persistent 401 must stop after one refresh");
        }
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Found, HttpStatusCode.ServiceUnavailable })
        {
            var client = new AccountClient(cookies => new StaticHandler(status, "{}"), TimeSpan.FromSeconds(1));
            Reject<Exception>(() => client.FetchAsync(Session(), CancellationToken.None).GetAwaiter().GetResult());
        }
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            Reject<OperationCanceledException>(() => new AccountClient(cookies => new StaticHandler(HttpStatusCode.OK, "{}"), TimeSpan.FromSeconds(1))
                .FetchAsync(Session(), cancel.Token).GetAwaiter().GetResult());
        }
    }

    private static void TestSerializedRefresh()
    {
        var session = Session();
        var handler = new AccountHandler(true);
        var client = new AccountClient(cookies => { handler.Cookies = cookies; return handler; }, TimeSpan.FromSeconds(3));
        Task<AccountFetchResult> first = client.FetchAsync(session, CancellationToken.None);
        Task<AccountFetchResult> second = client.FetchAsync(session, CancellationToken.None);
        Task.WaitAll(first, second);
        Assert(handler.Refreshes == 1 && second.Result.Session.RefreshToken == "rotated-refresh", "queued requests must use the new refresh cookie instead of replaying the old credential");
    }

    private static AccountSession Session() { return new AccountSession { AccessToken = "fixture-access", RefreshToken = "fixture-refresh" }; }
    private static void Assert(bool condition, string name) { if (!condition) throw new InvalidOperationException("Account test failed: " + name); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Invalid account input was accepted."); }

    private sealed class AccountHandler : HttpMessageHandler
    {
        internal CookieContainer Cookies;
        internal int Refreshes, Logouts, AuthenticatedRequests;
        internal bool SafeRequests = true, Csrf, FailWallet, RejectAuthentication, Revoked, ExpireLogout;
        internal Action AfterRefreshResponse;
        internal string WalletMicroCny = "10000000";
        internal string ExpectedAccess;
        private readonly bool expireAccess;
        internal AccountHandler(bool expireAccess) { this.expireAccess = expireAccess; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            SafeRequests &= request.RequestUri.Scheme == "https" && request.RequestUri.Host == "xai-tools.cn" && request.RequestUri.Port == 443 &&
                request.Headers.Authorization == null && !request.Headers.Contains("X-Api-Key");
            string path = request.RequestUri.AbsolutePath;
            if (path == "/api/auth/refresh")
            {
                Refreshes++;
                Csrf = request.Headers.Contains("X-CSRF-Protection") && String.Join("", request.Headers.GetValues("X-CSRF-Protection")) == "1";
                Assert(Csrf && request.Method == HttpMethod.Post, "refresh must use the official mutation contract");
                Cookies.Add(new Uri("https://xai-tools.cn/"), new Cookie(AccountClient.AccessCookie, "rotated-access", "/") { Secure = true, HttpOnly = true });
                Cookies.Add(new Uri("https://xai-tools.cn/"), new Cookie(AccountClient.RefreshCookie, "rotated-refresh", "/") { Secure = true, HttpOnly = true });
                return RefreshResponseAsync();
            }
            if (path == "/api/auth/logout")
            {
                Logouts++;
                Csrf = request.Headers.Contains("X-CSRF-Protection");
                if (ExpireLogout && Cookies.GetCookies(new Uri("https://xai-tools.cn/"))[AccountClient.AccessCookie].Value != "rotated-access")
                    return Response(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"authentication_required\"}}");
                return Response(HttpStatusCode.OK, "{\"success\":true}");
            }
            AuthenticatedRequests++;
            string access = Cookies.GetCookies(new Uri("https://xai-tools.cn/"))[AccountClient.AccessCookie].Value;
            if (ExpectedAccess != null) Assert(access == ExpectedAccess, "wallet refresh must use cookies returned from recharge");
            if (RejectAuthentication || (expireAccess && access != "rotated-access"))
                return Response(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"" + (Revoked ? "session_revoked" : "authentication_required") + "\"}}");
            if (path == "/api/auth/me") return Response(HttpStatusCode.OK, "{\"id\":\"fixture-user\",\"email\":\"fixture@example.test\"}");
            if (path == "/api/user/api-keys") return Response(HttpStatusCode.OK, KeyFixture);
            if (path == "/api/user/wallet") return FailWallet ? Response(HttpStatusCode.ServiceUnavailable, "{}") :
                Response(HttpStatusCode.OK, "{\"balanceMicroCny\":\"" + WalletMicroCny + "\",\"reservedMicroCny\":\"2000000\"}");
            throw new InvalidOperationException("Unexpected account endpoint.");
        }
        protected override void Dispose(bool disposing) { /* Shared fixture simulates successive transports. */ }

        private async Task<HttpResponseMessage> RefreshResponseAsync()
        {
            var response = await Response(HttpStatusCode.Created, "{\"authenticated\":true,\"expiresIn\":900}").ConfigureAwait(false);
            if (AfterRefreshResponse != null) AfterRefreshResponse();
            return response;
        }
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode status;
        private readonly string body;
        internal StaticHandler(HttpStatusCode status, string body) { this.status = status; this.body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Response(status, body); }
    }
    private static async Task<HttpResponseMessage> Response(HttpStatusCode status, string body)
    {
        // Force asynchronous overlap so the serialization check detects replay
        // of the original refresh cookie instead of passing synchronously.
        await Task.Delay(3).ConfigureAwait(false);
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}
