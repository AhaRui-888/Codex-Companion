using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class OfficialLogin
{
    internal const string Origin = "https://xai-tools.cn";
    internal const string LoginUrl = Origin + "/#recharge";
    internal const string RuntimeDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/#download-section";

    internal static AccountSession Show(IWin32Window owner, string dataDirectory,
        Func<AccountSession, CancellationToken, Task<bool>> validateSession)
    {
        if (validateSession == null) throw new ArgumentNullException("validateSession");
        if (String.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("账户数据目录不能为空。", "dataDirectory");
        WebViewRuntime.InstallAssemblyResolver();
        return ShowBrowser(owner, Path.GetFullPath(dataDirectory), validateSession);
    }

    // Keep the SDK types out of Show's JIT compilation until AssemblyResolve is installed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AccountSession ShowBrowser(IWin32Window owner, string dataDirectory,
        Func<AccountSession, CancellationToken, Task<bool>> validateSession)
    {
        using (var dialog = new BrowserLoginForm(dataDirectory, validateSession))
            return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.ValidatedSession : null;
    }

    internal static void ShowRecharge(IWin32Window owner, string dataDirectory, AccountSession session,
        Action<AccountSession> sessionChanged)
    {
        if (session == null) throw new AccountAuthenticationException();
        session.Validate();
        if (sessionChanged == null) throw new ArgumentNullException("sessionChanged");
        WebViewRuntime.InstallAssemblyResolver();
        ShowRechargeBrowser(owner, Path.GetFullPath(dataDirectory), session, sessionChanged);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ShowRechargeBrowser(IWin32Window owner, string dataDirectory, AccountSession session,
        Action<AccountSession> sessionChanged)
    {
        using (var dialog = new BrowserLoginForm(dataDirectory, null, session.Copy(), sessionChanged))
            dialog.ShowDialog(owner);
    }

    internal static bool IsOfficialOrigin(string value)
    {
        Uri uri;
        return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
            uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && String.IsNullOrEmpty(uri.UserInfo) &&
            String.Equals(uri.DnsSafeHost, "xai-tools.cn", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsOfficialCookie(string domain, string path, bool secure, bool httpOnly)
    {
        return !String.IsNullOrEmpty(domain) &&
            (String.Equals(domain, "xai-tools.cn", StringComparison.OrdinalIgnoreCase) ||
             String.Equals(domain, ".xai-tools.cn", StringComparison.OrdinalIgnoreCase)) &&
            path == "/" && secure && httpOnly;
    }

    private sealed class BrowserLoginForm : Form
    {
        private readonly string dataDirectory;
        private readonly Func<AccountSession, CancellationToken, Task<bool>> validateSession;
        private readonly bool rechargeMode;
        private readonly Action<AccountSession> sessionChanged;
        private AccountSession rechargeSession;
        private bool rechargeSessionReady;
        private bool capturingRechargeSession;
        private bool rechargeCloseRequested;
        private bool rechargeCloseReady;
        private int authenticationRequests;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly CancellationToken closeToken;
        private readonly System.Windows.Forms.Timer cookieTimer = new System.Windows.Forms.Timer();
        private readonly WebView2 browser = new WebView2();
        private readonly Label status = new Label();
        private readonly Button retry = new Button();
        private readonly Button installRuntime = new Button();
        private CoreWebView2Environment environment;
        private string profileDirectory;
        private bool initializing;
        private bool initialized;
        private bool checkingSession;
        private bool closing;
        private bool resourcesDisposed;
        private bool documentLoaded;
        private bool sessionSynchronizationFailed;
        private DateTime nextValidationUtc = DateTime.MinValue;
        internal AccountSession ValidatedSession { get; private set; }

        internal BrowserLoginForm(string dataDirectory, Func<AccountSession, CancellationToken, Task<bool>> validateSession)
            : this(dataDirectory, validateSession, null, null) { }

        internal BrowserLoginForm(string dataDirectory, Func<AccountSession, CancellationToken, Task<bool>> validateSession,
            AccountSession rechargeSession, Action<AccountSession> sessionChanged)
        {
            this.dataDirectory = dataDirectory;
            this.validateSession = validateSession;
            this.rechargeSession = rechargeSession;
            this.sessionChanged = sessionChanged;
            rechargeMode = rechargeSession != null;
            closeToken = cancellation.Token;
            Text = rechargeMode ? "充值 xai-tools.cn · Codex Companion" : "登录 xai-tools.cn · Codex Companion";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(600, 500);
            Size working = Screen.FromControl(this).WorkingArea.Size;
            Size = new Size(Math.Min(1000, working.Width - 64), Math.Min(800, working.Height - 64));
            if (rechargeMode)
            {
                ClientSize = new Size(Math.Min(900, working.Width - 48), Math.Min(590, working.Height - 80));
                MinimumSize = SizeFromClientSize(new Size(Math.Min(760, ClientSize.Width), Math.Min(520, ClientSize.Height)));
            }
            BackColor = Color.FromArgb(246, 248, 251);
            ShowInTaskbar = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, rechargeMode ? 0 : 66));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, rechargeMode ? 62 : 82));
            var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(18, 10, 18, 8) };
            header.Controls.Add(new Label { Text = "https://xai-tools.cn", Dock = DockStyle.Bottom, Height = 23, ForeColor = Color.FromArgb(104, 117, 138) });
            header.Controls.Add(new Label { Text = rechargeMode ? "xai-tools.cn 充值中心" : "在 xai-tools.cn 官方页面登录", Dock = DockStyle.Top, Height = 27,
                Font = new Font(Font.FontFamily, 11F, FontStyle.Bold), ForeColor = Color.FromArgb(35, 48, 68) });
            browser.Dock = DockStyle.Fill;
            browser.Margin = Padding.Empty;
            browser.DefaultBackgroundColor = Color.White;
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(18, 8, 18, 8), Margin = Padding.Empty };
            if (rechargeMode) footer.Padding = new Padding(16, 3, 16, 3);
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            status.Dock = DockStyle.Fill;
            status.ForeColor = Color.FromArgb(104, 117, 138);
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Text = rechargeMode ? "正在打开充值页面…" : "正在打开官方登录页面…";
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            var cancel = new Button { Text = rechargeMode ? "关闭并刷新余额" : "取消", AutoSize = true, Height = 29, DialogResult = DialogResult.Cancel };
            retry.Text = "重试";
            retry.AutoSize = true;
            retry.Height = 29;
            retry.Visible = false;
            installRuntime.Text = "下载 WebView2";
            installRuntime.AutoSize = true;
            installRuntime.Height = 29;
            installRuntime.Visible = false;
            actions.Controls.Add(cancel);
            actions.Controls.Add(retry);
            actions.Controls.Add(installRuntime);
            footer.Controls.Add(status, 0, 0);
            footer.Controls.Add(actions, 0, 1);
            layout.Controls.Add(header, 0, 0);
            layout.Controls.Add(browser, 0, 1);
            layout.Controls.Add(footer, 0, 2);
            Controls.Add(layout);
            CancelButton = cancel;
            cookieTimer.Interval = 1500;
            cookieTimer.Tick += async delegate {
                if (rechargeMode) await CaptureRechargeSessionAsync();
                else await CheckSessionAsync(false);
            };
            Shown += async delegate { await InitializeAsync(); };
            retry.Click += async delegate
            {
                if (!initialized) await InitializeAsync();
                else if (rechargeMode || !documentLoaded) browser.CoreWebView2.Navigate(LoginUrl);
                else await CheckSessionAsync(true);
            };
            installRuntime.Click += delegate
            {
                try { Process.Start(new ProcessStartInfo(RuntimeDownloadUrl) { UseShellExecute = true }); }
                catch { status.Text = "请在浏览器打开 Microsoft WebView2 官网并安装 Evergreen Runtime。"; }
            };
        }

        private async Task InitializeAsync()
        {
            if (initializing || closing) return;
            initializing = true;
            retry.Visible = false;
            installRuntime.Visible = false;
            status.Text = rechargeMode ? "正在打开 xai-tools.cn 充值页面…" : "正在打开 xai-tools.cn 官方登录页面…";
            try
            {
                // SetLoaderDllFolderPath is process-wide and must be set before the first SDK call.
                PrepareSdk(dataDirectory);
                if (profileDirectory == null) profileDirectory = WebViewRuntime.CreateProfileDirectory(dataDirectory);
                var options = new CoreWebView2EnvironmentOptions { Language = "zh-CN", AllowSingleSignOnUsingOSPrimaryAccount = false };
                environment = await CoreWebView2Environment.CreateAsync(null, profileDirectory, options);
                // WebView2 policy/environment overrides must not redirect this window
                // into an existing browser's profile. Only our fresh folder is allowed.
                if (!String.Equals(Path.GetFullPath(environment.UserDataFolder).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(profileDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("WebView2 的浏览器数据目录被系统策略覆盖，无法创建独立登录窗口。");
                CoreWebView2Environment ownedEnvironment = environment;
                string ownedProfile = profileDirectory;
                EventHandler<CoreWebView2BrowserProcessExitedEventArgs> exited = null;
                exited = delegate
                {
                    ownedEnvironment.BrowserProcessExited -= exited;
                    WebViewRuntime.DeleteProfileLater(Path.GetDirectoryName(ownedProfile), ownedProfile);
                };
                ownedEnvironment.BrowserProcessExited += exited;
                if (closing) return;
                CoreWebView2ControllerOptions controllerOptions = environment.CreateCoreWebView2ControllerOptions();
                controllerOptions.ProfileName = "OfficialLogin";
                controllerOptions.IsInPrivateModeEnabled = true;
                await browser.EnsureCoreWebView2Async(environment, controllerOptions);
                if (closing) return;
                CoreWebView2 core = browser.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsWebMessageEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                if (rechargeMode)
                {
                    // Presentation only: keep the website's original controls, order
                    // requests, QR generation and payment-state reconciliation intact.
                    string presentation = PrivateFiles.Utf8.GetString(PrivateFiles.Resource("recharge-view.js"));
                    string styles = PrivateFiles.Utf8.GetString(PrivateFiles.Resource("recharge-view.css"));
                    presentation = presentation.Replace("/*COMPANION_STYLE*/", Json.Write(styles));
                    await core.AddScriptToExecuteOnDocumentCreatedAsync(presentation);
                    if (closing) return;
                    // Seed only the verified website session into our new private profile.
                    if (!String.IsNullOrEmpty(rechargeSession.AccessToken))
                        WriteSessionCookie(core.CookieManager, AccountClient.AccessCookie, rechargeSession.AccessToken, "xai-tools.cn");
                    if (!String.IsNullOrEmpty(rechargeSession.RefreshToken))
                        WriteSessionCookie(core.CookieManager, AccountClient.RefreshCookie, rechargeSession.RefreshToken, "xai-tools.cn");
                    rechargeSessionReady = true;
                    core.AddWebResourceRequestedFilter(Origin + "/api/auth/*", CoreWebView2WebResourceContext.All);
                    core.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs args)
                    {
                        if (IsAuthenticationMutation(args.Request.Uri)) authenticationRequests++;
                    };
                }
                core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs args)
                {
                    if (!IsOfficialOrigin(args.Uri)) { args.Cancel = true; return; }
                    documentLoaded = false;
                };
                core.NavigationCompleted += async delegate(object sender, CoreWebView2NavigationCompletedEventArgs args)
                {
                    if (closing) return;
                    documentLoaded = args.IsSuccess && IsOfficialOrigin(core.Source);
                    if (!documentLoaded)
                    {
                        status.Text = "官方页面加载失败，请检查网络后重试。";
                        retry.Visible = true;
                        return;
                    }
                    status.Text = rechargeMode ? "完成充值后关闭此窗口，余额将自动刷新。" : "请在官方页面完成登录。登录成功后将自动读取余额。";
                    retry.Visible = false;
                    if (rechargeMode) await CaptureRechargeSessionAsync();
                    else await CheckSessionAsync(true);
                };
                core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs args) { args.Handled = true; };
                core.DownloadStarting += delegate(object sender, CoreWebView2DownloadStartingEventArgs args) { args.Cancel = true; args.Handled = true; };
                core.PermissionRequested += delegate(object sender, CoreWebView2PermissionRequestedEventArgs args) { args.State = CoreWebView2PermissionState.Deny; };
                core.ServerCertificateErrorDetected += delegate(object sender, CoreWebView2ServerCertificateErrorDetectedEventArgs args) { args.Action = CoreWebView2ServerCertificateErrorAction.Cancel; };
                core.WebResourceResponseReceived += async delegate(object sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
                {
                    if (rechargeMode)
                    {
                        if (IsAuthenticationMutation(args.Request.Uri))
                        {
                            authenticationRequests = Math.Max(0, authenticationRequests - 1);
                            await CaptureRechargeSessionAsync();
                        }
                        return;
                    }
                    Uri uri;
                    if (!closing && args.Response.StatusCode >= 200 && args.Response.StatusCode < 300 &&
                        IsOfficialOrigin(args.Request.Uri) && Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out uri) &&
                        (uri.AbsolutePath == "/api/auth/login" || uri.AbsolutePath == "/api/auth/refresh"))
                        await CheckSessionAsync(true);
                };
                initialized = true;
                cookieTimer.Start();
                core.Navigate(LoginUrl);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                if (closing) return;
                status.Text = "需要 Microsoft Edge WebView2 Runtime。下载安装 Evergreen 后点击重试。";
                installRuntime.Visible = true;
                retry.Visible = true;
            }
            catch (Exception)
            {
                if (closing) return;
                status.Text = rechargeMode ? "无法打开充值窗口，请确认 WebView2 Runtime 可用后重试。" : "无法打开官方登录窗口，请确认 WebView2 Runtime 可用后重试。";
                installRuntime.Visible = true;
                retry.Visible = true;
            }
            finally
            {
                initializing = false;
                if (closing && profileDirectory != null)
                    WebViewRuntime.DeleteProfileLater(Path.GetDirectoryName(profileDirectory), profileDirectory);
            }
        }

        private async Task CheckSessionAsync(bool force)
        {
            if (rechargeMode || closing || sessionSynchronizationFailed || !initialized || !documentLoaded || checkingSession ||
                (!force && DateTime.UtcNow < nextValidationUtc) || !IsOfficialOrigin(browser.CoreWebView2.Source)) return;
            checkingSession = true;
            try
            {
                // HttpOnly cookies are intentionally read through CookieManager, never page script.
                // Do not accept sessionStorage hints, window messages, or another website's token.
                var cookies = await browser.CoreWebView2.CookieManager.GetCookiesAsync(Origin + "/");
                if (closing || !documentLoaded || !IsOfficialOrigin(browser.CoreWebView2.Source)) return;
                string access = null;
                string refresh = null;
                string accessDomain = null;
                string refreshDomain = null;
                foreach (CoreWebView2Cookie cookie in cookies)
                {
                    if (!IsOfficialCookie(cookie.Domain, cookie.Path, cookie.IsSecure, cookie.IsHttpOnly)) continue;
                    if (cookie.Name == AccountClient.AccessCookie) { if (access != null) return; access = cookie.Value; accessDomain = cookie.Domain; }
                    if (cookie.Name == AccountClient.RefreshCookie) { if (refresh != null) return; refresh = cookie.Value; refreshDomain = cookie.Domain; }
                }
                // An expired access cookie can disappear while the refresh cookie remains valid.
                if (String.IsNullOrEmpty(access) && String.IsNullOrEmpty(refresh)) return;
                var candidate = new AccountSession { AccessToken = access, RefreshToken = refresh };
                candidate.Validate();
                status.Text = "正在验证账户并读取余额…";
                retry.Visible = false;
                nextValidationUtc = DateTime.UtcNow.AddSeconds(8);
                bool valid = await ValidateAndSynchronizeAsync(candidate, accessDomain, refreshDomain);
                if (closing || sessionSynchronizationFailed || closeToken.IsCancellationRequested || !documentLoaded || !IsOfficialOrigin(browser.CoreWebView2.Source)) return;
                if (!valid)
                {
                    status.Text = "尚未完成账户验证，请继续在官方页面登录或重试。";
                    retry.Visible = true;
                    return;
                }
                ValidatedSession = candidate;
                cookieTimer.Stop();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (OperationCanceledException)
            {
                if (closing || closeToken.IsCancellationRequested || sessionSynchronizationFailed) return;
                nextValidationUtc = DateTime.UtcNow.AddSeconds(8);
                status.Text = "账户验证超时，请检查网络后点击重试。";
                retry.Visible = true;
            }
            catch (Exception)
            {
                if (closing || sessionSynchronizationFailed) return;
                nextValidationUtc = DateTime.UtcNow.AddSeconds(8);
                status.Text = "账户验证暂时失败，请检查网络后点击重试。";
                retry.Visible = true;
            }
            finally { checkingSession = false; }
        }

        private async Task<bool> ValidateAndSynchronizeAsync(AccountSession candidate, string accessDomain, string refreshDomain)
        {
            string originalAccess = candidate.AccessToken;
            string originalRefresh = candidate.RefreshToken;
            bool valid = false;
            Exception validationError = null;
            try { valid = await validateSession(candidate, closeToken); }
            catch (Exception error) { validationError = error; }

            // AccountClient rotates candidate in place before fetching the wallet. Keep the
            // browser current even when that later request fails or times out: replaying the
            // previous refresh cookie would revoke the entire login session.
            if (!closing && !browser.IsDisposed && browser.CoreWebView2 != null &&
                (candidate.AccessToken != originalAccess || candidate.RefreshToken != originalRefresh))
            {
                try
                {
                    AccountSession.ValidateToken(candidate.AccessToken);
                    AccountSession.ValidateToken(candidate.RefreshToken);
                    var manager = browser.CoreWebView2.CookieManager;
                    WriteSessionCookie(manager, AccountClient.AccessCookie, candidate.AccessToken,
                        accessDomain ?? refreshDomain ?? "xai-tools.cn");
                    WriteSessionCookie(manager, AccountClient.RefreshCookie, candidate.RefreshToken,
                        refreshDomain ?? accessDomain ?? "xai-tools.cn");
                    var synchronized = await manager.GetCookiesAsync(Origin + "/");
                    if (closing) return false;
                    int accessMatches = 0;
                    int refreshMatches = 0;
                    foreach (CoreWebView2Cookie cookie in synchronized)
                    {
                        if (!IsOfficialCookie(cookie.Domain, cookie.Path, cookie.IsSecure, cookie.IsHttpOnly)) continue;
                        if (!cookie.IsSession || cookie.SameSite != CoreWebView2CookieSameSiteKind.Strict) continue;
                        if (cookie.Name == AccountClient.AccessCookie && cookie.Value == candidate.AccessToken) accessMatches++;
                        if (cookie.Name == AccountClient.RefreshCookie && cookie.Value == candidate.RefreshToken) refreshMatches++;
                    }
                    if (accessMatches != 1 || refreshMatches != 1)
                        throw new InvalidOperationException("登录会话未同步。");
                }
                catch (Exception)
                {
                    if (closing) return false;
                    // Do not allow a retry to read and reuse a stale, single-use refresh cookie.
                    sessionSynchronizationFailed = true;
                    cookieTimer.Stop();
                    retry.Visible = false;
                    browser.Enabled = false;
                    try { browser.CoreWebView2.Stop(); browser.CoreWebView2.CookieManager.DeleteAllCookies(); }
                    catch { }
                    status.Text = "登录会话同步失败，请取消并重新打开登录窗口。";
                    return false;
                }
            }
            if (validationError != null) throw validationError;
            return valid;
        }

        private static void WriteSessionCookie(CoreWebView2CookieManager manager, string name, string value, string domain)
        {
            CoreWebView2Cookie cookie = manager.CreateCookie(name, value, domain, "/");
            cookie.IsSecure = true;
            cookie.IsHttpOnly = true;
            cookie.SameSite = CoreWebView2CookieSameSiteKind.Strict;
            cookie.Expires = DateTime.MinValue;
            manager.AddOrUpdateCookie(cookie);
        }

        private static bool IsAuthenticationMutation(string address)
        {
            Uri uri;
            return IsOfficialOrigin(address) && Uri.TryCreate(address, UriKind.Absolute, out uri) &&
                (uri.AbsolutePath == "/api/auth/refresh" || uri.AbsolutePath == "/api/auth/login" || uri.AbsolutePath == "/api/auth/logout");
        }

        private async Task CaptureRechargeSessionAsync()
        {
            if (!rechargeSessionReady || closing || rechargeCloseRequested || capturingRechargeSession || authenticationRequests != 0) return;
            capturingRechargeSession = true;
            try { await ReadRechargeSessionAsync(); }
            catch { if (!closing) status.Text = "暂时无法同步登录状态，关闭窗口后将重新验证。"; }
            finally { capturingRechargeSession = false; }
        }

        private async Task ReadRechargeSessionAsync()
        {
            var cookies = await browser.CoreWebView2.CookieManager.GetCookiesAsync(Origin + "/");
            if (closing) return;
            if (authenticationRequests != 0) throw new InvalidOperationException("登录状态正在更新。");
            string access = null, refresh = null;
            foreach (CoreWebView2Cookie cookie in cookies)
            {
                if (!IsOfficialCookie(cookie.Domain, cookie.Path, cookie.IsSecure, cookie.IsHttpOnly)) continue;
                if (cookie.Name == AccountClient.AccessCookie) { if (access != null) throw new InvalidDataException(); access = cookie.Value; }
                if (cookie.Name == AccountClient.RefreshCookie) { if (refresh != null) throw new InvalidDataException(); refresh = cookie.Value; }
            }
            AccountSession current = null;
            if (!String.IsNullOrEmpty(access) || !String.IsNullOrEmpty(refresh))
            {
                current = new AccountSession { AccessToken = access, RefreshToken = refresh };
                current.Validate();
            }
            if (current == null || rechargeSession == null || current.AccessToken != rechargeSession.AccessToken || current.RefreshToken != rechargeSession.RefreshToken)
            {
                rechargeSession = current;
                sessionChanged(current);
            }
        }

        private async Task CloseRechargeAsync()
        {
            rechargeCloseRequested = true;
            cookieTimer.Stop();
            browser.Enabled = false;
            status.Text = "正在同步登录状态并关闭…";
            try
            {
                // Let an in-flight cookie read or website refresh finish before handing
                // ownership back. If it cannot finish, discard the old single-use token.
                var timeout = Stopwatch.StartNew();
                while ((capturingRechargeSession || authenticationRequests != 0) && timeout.ElapsedMilliseconds < 5000)
                    await Task.Delay(50);
                browser.CoreWebView2.Stop();
                if (authenticationRequests != 0) sessionChanged(null);
                else await ReadRechargeSessionAsync();
            }
            catch { sessionChanged(null); }
            finally
            {
                rechargeCloseReady = true;
                DialogResult = DialogResult.Cancel;
                Close();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs args)
        {
            base.OnFormClosing(args);
            if (args.Cancel) return;
            if (rechargeMode && rechargeSessionReady && !rechargeCloseReady)
            {
                args.Cancel = true;
                DialogResult = DialogResult.None;
                if (!rechargeCloseRequested) { var completion = CloseRechargeAsync(); }
                return;
            }
            closing = true;
            cancellation.Cancel();
            cookieTimer.Stop();
            if (browser.CoreWebView2 != null)
            {
                try { browser.CoreWebView2.CookieManager.DeleteAllCookies(); browser.CoreWebView2.Stop(); }
                catch { }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !resourcesDisposed)
            {
                resourcesDisposed = true;
                closing = true;
                cancellation.Cancel();
                cookieTimer.Dispose();
                browser.Dispose();
                cancellation.Dispose();
                if (profileDirectory != null) WebViewRuntime.DeleteProfileLater(Path.GetDirectoryName(profileDirectory), profileDirectory);
            }
            base.Dispose(disposing);
        }
    }

    private static readonly object LoaderGate = new object();
    private static bool loaderPrepared;
    private static void PrepareSdk(string dataDirectory)
    {
        lock (LoaderGate)
        {
            if (loaderPrepared) return;
            CoreWebView2Environment.SetLoaderDllFolderPath(WebViewRuntime.PrepareLoaderDirectory(dataDirectory));
            loaderPrepared = true;
        }
    }
}
