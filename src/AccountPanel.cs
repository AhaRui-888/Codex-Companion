using System;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class AccountPanel : DesktopSurface
{
    private readonly bool previewMode;
    private readonly AccountClient client;
    private readonly AccountSessionStore store;
    private readonly Action<IWin32Window, string, AccountSession, Action<AccountSession>> showRecharge;
    private readonly Label balanceLabel;
    private readonly Label accountLabel;
    private readonly Label accountStatusLabel;
    private readonly Button loginButton;
    private readonly Button refreshBalanceButton;
    private readonly Button rechargeButton;
    private readonly Button logoutButton;
    private readonly ToolTip balanceTooltip = new ToolTip();
    private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer { Interval = 60000 };
    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    private AccountSession session;
    private AccountSnapshot snapshot;
    private bool started;
    private bool fetching;
    private bool signingIn;
    private bool recharging;
    private bool operationsEnabled = true;
    private int generation;

    internal AccountPanel(bool previewMode, string dataDirectory)
        : this(previewMode, new AccountClient(), new AccountSessionStore(dataDirectory)) { }

    internal AccountPanel(bool previewMode, AccountClient client, AccountSessionStore store)
        : this(previewMode, client, store, OfficialLogin.ShowRecharge) { }

    internal AccountPanel(bool previewMode, AccountClient client, AccountSessionStore store,
        Action<IWin32Window, string, AccountSession, Action<AccountSession>> showRecharge)
    {
        this.previewMode = previewMode;
        this.client = client;
        this.store = store;
        this.showRecharge = showRecharge;
        Font = new Font("Segoe UI", 13F, FontStyle.Regular, GraphicsUnit.Pixel);
        Name = "accountPanel";
        AccessibleName = "网站账户与余额";
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 4, 0, 8);
        Padding = new Padding(16, 10, 16, 10);
        CornerRadius = 10;
        balanceLabel = Label("登录后查看余额", 17, DesktopPalette.Text);
        balanceLabel.Font = new Font(Font.FontFamily, 17, FontStyle.Bold, GraphicsUnit.Pixel);
        balanceLabel.AccessibleName = "账户可用余额";
        accountLabel = Label("xai-tools.cn 账户", 11, DesktopPalette.Muted);
        accountLabel.AccessibleName = "当前登录账户";
        accountStatusLabel = Label("", 10, DesktopPalette.Muted);
        accountStatusLabel.AccessibleName = "余额更新状态";
        loginButton = Button("登录");
        loginButton.AccessibleName = "登录 xai-tools.cn";
        refreshBalanceButton = Button("刷新余额");
        rechargeButton = Button("充值");
        rechargeButton.AccessibleName = "登录后在 xai-tools.cn 充值";
        balanceTooltip.SetToolTip(rechargeButton, "登录并获取余额后可充值，关闭充值窗口后自动刷新余额。");
        logoutButton = Button("退出登录");
        Controls.AddRange(new Control[] { balanceLabel, accountLabel, accountStatusLabel, loginButton, rechargeButton, refreshBalanceButton, logoutButton });
        loginButton.Click += LoginClicked;
        refreshBalanceButton.Click += async delegate { await RefreshAsync(true); };
        rechargeButton.Click += async delegate { await RechargeAsync(); };
        logoutButton.Click += async delegate { await LogoutAsync(); };
        refreshTimer.Tick += async delegate { await RefreshAsync(false); };
        Resize += delegate { LayoutAccount(); };
        FontChanged += delegate { LayoutAccount(); };
        SetButtons();
        LayoutAccount();
    }

    private Label Label(string text, float size, Color color)
    {
        return new Label { Text = text, AutoEllipsis = true, UseMnemonic = false, BackColor = Color.Transparent,
            ForeColor = color, Font = new Font(Font.FontFamily, size, FontStyle.Regular, GraphicsUnit.Pixel),
            TextAlign = ContentAlignment.MiddleLeft, UseCompatibleTextRendering = true, Margin = Padding.Empty };
    }

    private Button Button(string text)
    {
        return new DesktopButton { Text = text, Margin = Padding.Empty, CornerRadius = 6,
            Font = new Font(Font.FontFamily, 12, FontStyle.Regular, GraphicsUnit.Pixel) };
    }

    private int Pixels(int value) { return (int)Math.Round(value * Font.Size / 13F); }

    internal int RequiredRowHeight(int width) { return Pixels(width < Pixels(540) ? 124 : 90); }

    private void LayoutAccount()
    {
        if (balanceLabel == null || loginButton == null) return;
        int left = Padding.Left, top = Padding.Top;
        int buttonWidth = Pixels(84), gap = Pixels(8), buttonHeight = Pixels(30);
        int buttonRight = ClientSize.Width - Padding.Right;
        bool compact = ClientSize.Width < Pixels(540);
        int buttonTop = compact ? top + Pixels(62) : top + Pixels(8);
        if (session == null)
        {
            loginButton.SetBounds(buttonRight - buttonWidth, buttonTop, buttonWidth, buttonHeight);
            rechargeButton.SetBounds(loginButton.Left - gap - Pixels(60), buttonTop, Pixels(60), buttonHeight);
        }
        else
        {
            logoutButton.SetBounds(buttonRight - buttonWidth, buttonTop, buttonWidth, buttonHeight);
            refreshBalanceButton.SetBounds(logoutButton.Left - gap - buttonWidth, buttonTop, buttonWidth, buttonHeight);
            rechargeButton.SetBounds(refreshBalanceButton.Left - gap - Pixels(60), buttonTop, Pixels(60), buttonHeight);
        }
        int textWidth = compact ? ClientSize.Width - Padding.Horizontal : Math.Max(0, rechargeButton.Left - gap - left);
        balanceLabel.SetBounds(left, top, textWidth, Pixels(23));
        accountLabel.SetBounds(left, top + Pixels(24), textWidth, Pixels(17));
        accountStatusLabel.SetBounds(left, top + Pixels(42), ClientSize.Width - Padding.Horizontal, Pixels(16));
    }

    internal async Task StartAsync()
    {
        if (started || previewMode || IsDisposed) return;
        started = true;
        try
        {
            session = store.Load();
            if (session != null) { balanceLabel.Text = "余额加载中…"; SetButtons(); await RefreshAsync(false); }
        }
        catch (Exception) { accountStatusLabel.Text = "本机登录信息不可用，请重新登录。"; }
        if (!IsDisposed) refreshTimer.Start();
    }

    internal void SetOperationsEnabled(bool enabled)
    { operationsEnabled = enabled; SetButtons(); }

    private void SetButtons()
    {
        bool enabled = operationsEnabled && !previewMode && !fetching && !signingIn && !recharging && !lifetime.IsCancellationRequested;
        loginButton.Visible = session == null;
        refreshBalanceButton.Visible = session != null;
        logoutButton.Visible = session != null;
        loginButton.Enabled = enabled;
        refreshBalanceButton.Enabled = enabled;
        logoutButton.Enabled = enabled;
        rechargeButton.Enabled = enabled && session != null && snapshot != null;
        LayoutAccount();
    }

    private void LoginClicked(object sender, EventArgs e)
    {
        if (previewMode || signingIn || fetching || recharging || !operationsEnabled) return;
        signingIn = true;
        SetButtons();
        AccountFetchResult verified = null;
        try
        {
            AccountSession candidate = OfficialLogin.Show(FindForm(), store.DirectoryPath, async (cookies, token) => {
                try { verified = await client.FetchAsync(cookies, token); return true; }
                catch (AccountAuthenticationException) { return false; }
            });
            if (IsDisposed || lifetime.IsCancellationRequested) return;
            if (candidate == null || verified == null) { accountStatusLabel.Text = "已取消登录。"; return; }
            generation++;
            session = verified.Session;
            snapshot = verified.Snapshot;
            // The live balance remains usable if saving the session is unavailable.
            bool saved = SaveSessionSafely(session);
            ShowSnapshot();
            if (!saved) accountStatusLabel.Text = "已登录；登录信息保存失败，下次启动需重新登录。";
        }
        catch (Exception error)
        {
            if (!IsDisposed)
            {
                accountStatusLabel.Text = "登录未完成，请重试。";
                MessageBox.Show(FindForm(), error is InvalidOperationException ? error.Message : "无法完成网页登录，请检查网络连接后重试。",
                    "登录 xai-tools.cn", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        finally { signingIn = false; if (!IsDisposed) SetButtons(); }
    }

    internal async Task RefreshAsync(bool manual)
    {
        if (previewMode || session == null || fetching || signingIn || recharging || IsDisposed || lifetime.IsCancellationRequested) return;
        fetching = true;
        int requestGeneration = generation;
        AccountSession requestSession = session;
        bool sessionSaveFailed = false;
        accountStatusLabel.Text = "正在更新余额…";
        SetButtons();
        try
        {
            AccountFetchResult result = await client.FetchAsync(requestSession, lifetime.Token, updated => {
                // A successful refresh invalidates the saved cookie immediately.
                // Persist its replacement even if the window closes before this callback.
                sessionSaveFailed = !SaveSessionSafely(updated);
            });
            if (IsDisposed || lifetime.IsCancellationRequested || requestGeneration != generation) return;
            session = result.Session;
            snapshot = result.Snapshot;
            sessionSaveFailed = !SaveSessionSafely(session);
            ShowSnapshot();
            if (sessionSaveFailed) accountStatusLabel.Text = "余额已更新；登录信息保存失败，下次启动需重新登录。";
        }
        catch (AccountAuthenticationException)
        {
            if (IsDisposed || requestGeneration != generation) return;
            session = null; snapshot = null; generation++;
            try { store.Clear(); } catch { }
            ShowSignedOut();
            accountStatusLabel.Text = "登录已过期，请重新登录。";
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && !lifetime.IsCancellationRequested && requestGeneration == generation)
                accountStatusLabel.Text = snapshot == null ? "余额请求超时，请点击刷新重试。" : "请求超时；当前显示上次余额，请点击刷新重试。";
        }
        catch (Exception)
        {
            if (!IsDisposed && requestGeneration == generation)
                accountStatusLabel.Text = sessionSaveFailed ? "余额刷新失败；登录信息保存失败，下次启动需重新登录。" :
                    (snapshot == null ? "余额获取失败，请点击刷新重试。" : "刷新失败；当前显示上次余额，请点击刷新重试。");
        }
        finally { fetching = false; if (!IsDisposed) SetButtons(); }
    }

    internal async Task LogoutAsync()
    {
        if (previewMode || session == null || signingIn || fetching || recharging || IsDisposed) return;
        AccountSession previous = session;
        try { store.Clear(); }
        catch (Exception) { accountStatusLabel.Text = "本机登录信息清除失败，请重试退出。"; return; }
        generation++; session = null; snapshot = null;
        fetching = true;
        ShowSignedOut(); SetButtons();
        try
        {
            await client.LogoutAsync(previous, lifetime.Token);
            if (!IsDisposed) accountStatusLabel.Text = "已退出登录。";
        }
        catch (Exception)
        { if (!IsDisposed) accountStatusLabel.Text = "已清除本机登录；网络不可用，网站会话未能撤销。"; }
        finally { fetching = false; if (!IsDisposed) SetButtons(); }
    }

    internal async Task RechargeAsync()
    {
        if (previewMode || session == null || snapshot == null || fetching || signingIn || recharging ||
            !operationsEnabled || IsDisposed || lifetime.IsCancellationRequested) return;
        recharging = true;
        SetButtons();
        bool opened = false;
        try
        {
            accountStatusLabel.Text = "正在验证登录状态…";
            var current = await client.FetchAsync(session, lifetime.Token, updated => SaveSessionSafely(updated));
            if (IsDisposed || lifetime.IsCancellationRequested) return;
            session = current.Session;
            snapshot = current.Snapshot;
            SaveSessionSafely(session);
            ShowSnapshot();
            // The website exclusively owns refresh-token rotation while its modal is open.
            showRecharge(FindForm(), store.DirectoryPath, session.Copy(), updated => {
                if (updated == null)
                {
                    session = null;
                    snapshot = null;
                    generation++;
                    try { store.Clear(); } catch { }
                }
                else { session = updated.Copy(); SaveSessionSafely(session); }
            });
            opened = true;
        }
        catch (AccountAuthenticationException)
        {
            session = null; snapshot = null; generation++;
            try { store.Clear(); } catch { }
            if (!IsDisposed) { ShowSignedOut(); accountStatusLabel.Text = "登录已过期，请重新登录后充值。"; }
        }
        catch (Exception)
        { if (!IsDisposed) accountStatusLabel.Text = "无法打开充值，请检查网络后重试。"; }
        finally
        {
            recharging = false;
            if (!IsDisposed) SetButtons();
        }
        if (!opened || IsDisposed || lifetime.IsCancellationRequested) return;
        if (session == null)
        {
            ShowSignedOut();
            accountStatusLabel.Text = "登录已失效，请重新登录以查看最新余额。";
        }
        else await RefreshAsync(true);
    }

    private void ShowSignedOut()
    {
        balanceLabel.Text = "登录后查看余额";
        accountLabel.Text = "xai-tools.cn 账户";
        balanceTooltip.SetToolTip(balanceLabel, null);
    }

    private bool SaveSessionSafely(AccountSession value)
    {
        try { store.Save(value); return true; }
        catch
        {
            // A refresh cookie is single-use. Never knowingly leave an older
            // saved credential behind after a persistence failure.
            try { store.Clear(); } catch { }
            return false;
        }
    }

    private void ShowSnapshot()
    {
        balanceLabel.Text = "可用余额 ¥" + snapshot.AvailableBalance.ToString("N2", CultureInfo.GetCultureInfo("zh-CN"));
        balanceTooltip.SetToolTip(balanceLabel, "可用余额 ¥" + snapshot.AvailableBalance.ToString("N6", CultureInfo.GetCultureInfo("zh-CN")));
        accountLabel.Text = MaskAccount(snapshot.AccountName);
        accountStatusLabel.Text = "更新于 " + snapshot.UpdatedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (snapshot.Reserved != 0)
            accountStatusLabel.Text += " · 预留 ¥" + snapshot.Reserved.ToString("N2", CultureInfo.GetCultureInfo("zh-CN"));
    }

    internal static string MaskAccount(string account)
    {
        if (String.IsNullOrEmpty(account)) return "已登录";
        int at = account.IndexOf('@');
        if (at >= 0) return account.Substring(0, Math.Min(at, 2)) + "***" + account.Substring(at);
        if (account.Length > 7) return account.Substring(0, 3) + "****" + account.Substring(account.Length - 4);
        return "账户 " + account;
    }

    internal void SetPreviewSnapshot(AccountSnapshot value)
    {
        if (!previewMode) throw new InvalidOperationException("Only isolated preview data is supported.");
        snapshot = value;
        session = new AccountSession { AccessToken = "preview-fictional-session" };
        ShowSnapshot(); SetButtons();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { generation++; lifetime.Cancel(); refreshTimer.Dispose(); balanceTooltip.Dispose(); }
        base.Dispose(disposing);
    }
}
