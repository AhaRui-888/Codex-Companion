using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

public class MainForm : Form
{
    private readonly AppController controller;
    private readonly bool previewMode;
    private readonly IThemeGalleryService themeGalleryService;
    // Presentation values only; relay and credential behavior remains in AppController.
    private readonly Color accent = Color.FromArgb(25, 104, 223);
    private readonly Color accentDark = Color.FromArgb(25, 104, 223);
    private readonly Color pageBack = Color.FromArgb(246, 248, 251);
    private readonly Color panelBack = DesktopPalette.Surface;
    private readonly Color inputBack = DesktopPalette.Input;
    private readonly Color textColor = Color.FromArgb(35, 48, 68);
    private readonly Color mutedColor = Color.FromArgb(104, 117, 138);
    private readonly Color borderColor = Color.FromArgb(224, 230, 239);
    private const int BaseClientHeight = 336;
    private const int MaximumContentWidth = 820;
    private const int MinimumClientWidth = 440;
    private const int AdvertisementRow = 2;
    private const int ConnectionRow = 3;
    private const int StatusRow = 4;
    private const float PreviewScale = 1.5F;

    private TextBox apiKeyTextBox;
    private Action layoutCredentialOverlays;
    private Label statusLabel;
    private Label credentialHintLabel;
    private Button applyButton;
    private Button openCodexButton;
    private Button restoreButton;
    private Button appearanceButton;
    private Button officialWebsiteButton;
    private Button updateButton;
    private Button eyeButton;
    private TableLayoutPanel rootLayout;
    private AccountPanel accountPanel;
    private Panel layoutViewport;
    private Panel headerPanel;
    private Label brandLabel;
    private Label endpointAddress;
    private FlowLayoutPanel headerLinks;
    private TableLayoutPanel connectionLayout;
    private Control connectionPanel;
    private Panel connectionActions;
    private Label restartNote;
    private bool responsiveLayoutReady;
    private bool updatingResponsiveLayout;
    private bool compactHeader;
    private bool compactActions;
    private AdvertisementBanner advertisementBanner;
    private readonly CancellationTokenSource advertisementCancellation = new CancellationTokenSource();
    private readonly System.Windows.Forms.Timer advertisementRefreshTimer = new System.Windows.Forms.Timer();
    private bool loadingAdvertisements;
    private int advertisementWindowHeightAdjustment;
    private int advertisementWindowRowHeight;
    private bool showingApiKey;
    private bool checkingUpdate;
    private PendingUpdate pendingUpdate;

    public MainForm(AppController controller, bool previewMode)
        : this(controller, previewMode, null)
    {
    }

    public MainForm(AppController controller, bool previewMode, IThemeGalleryService themeGalleryService)
    {
        this.controller = controller;
        this.previewMode = previewMode;
        this.themeGalleryService = themeGalleryService;

        InitializeForm();
        BuildLayout();
        ApplyPreviewScale();
        LoadInitialState();
        if (!previewMode)
        {
            Shown += async delegate { await accountPanel.StartAsync(); };
            Shown += async delegate { await LoadAdvertisementsAsync(); };
            Shown += async delegate { await CheckUpdatesAsync(false); };
            advertisementRefreshTimer.Interval = 60000;
            advertisementRefreshTimer.Tick += async delegate { await LoadAdvertisementsAsync(); };
            advertisementRefreshTimer.Start();
        }
    }

    public void SavePreview(string path)
    {
        using (Bitmap bitmap = new Bitmap(this.Width, this.Height))
        {
            this.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(path);
        }
    }

    private void InitializeForm()
    {
        Text = "codex官方中转站";
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = true;
        Font = new Font("Segoe UI", 13F, FontStyle.Regular, GraphicsUnit.Pixel);
        BackColor = pageBack;
        ForeColor = textColor;
        ClientSize = new Size(620, BaseClientHeight);
        MinimumSize = SizeFromClientSize(new Size(MinimumClientWidth, BaseClientHeight));
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DesktopPalette.LightTitleBar(this);
    }

    private void BuildLayout()
    {
        layoutViewport = new Panel { Dock = DockStyle.Fill, BackColor = pageBack, AutoScroll = true };
        layoutViewport.Layout += delegate { UpdateResponsiveLayout(); };
        Controls.Add(layoutViewport);
        rootLayout = new TableLayoutPanel {
            BackColor = pageBack, Padding = new Padding(16, 8, 16, 12),
            ColumnCount = 1, RowCount = 5, Margin = Padding.Empty,
            Size = new Size(620, BaseClientHeight)
        };
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 152));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layoutViewport.Controls.Add(rootLayout);
        rootLayout.Controls.Add(BuildHeader(), 0, 0);
        accountPanel = new AccountPanel(previewMode, controller.AccountDirectory);
        rootLayout.Controls.Add(accountPanel, 0, 1);
        advertisementBanner = new AdvertisementBanner();
        rootLayout.Controls.Add(advertisementBanner, 0, AdvertisementRow);

        rootLayout.Controls.Add(BuildConnectionSection(), 0, ConnectionRow);
        rootLayout.Controls.Add(BuildStatusBar(), 0, StatusRow);
    }

    private Label CreateLabel(string text, float size, Color color)
    {
        return new Label { Text = text, ForeColor = color, BackColor = Color.Transparent,
            Font = new Font(Font.FontFamily, size, FontStyle.Regular, GraphicsUnit.Pixel),
            Margin = Padding.Empty, UseMnemonic = false, UseCompatibleTextRendering = true, TextAlign = ContentAlignment.MiddleLeft };
    }

    private void ApplyPreviewScale()
    {
        // Match the approved preview's visual size, including pixel-based fonts.
        var fonts = new Dictionary<Control, Font>();
        CaptureFonts(this, fonts);
        Scale(new SizeF(PreviewScale, PreviewScale));
        foreach (var item in fonts)
        {
            item.Key.Font = new Font(item.Value.FontFamily, item.Value.Size * PreviewScale,
                item.Value.Style, item.Value.Unit);
            var surface = item.Key as DesktopSurface;
            if (surface != null) surface.CornerRadius = PreviewPixels(surface.CornerRadius);
            var button = item.Key as DesktopButton;
            if (button != null) button.CornerRadius = PreviewPixels(button.CornerRadius);
        }
        ClientSize = new Size(PreviewPixels(620), PreviewPixels(BaseClientHeight));
        MinimumSize = SizeFromClientSize(new Size(PreviewPixels(MinimumClientWidth), PreviewPixels(BaseClientHeight)));
        responsiveLayoutReady = true;
        UpdateResponsiveLayout();
        PerformLayout();
        layoutCredentialOverlays();
    }

    private static void CaptureFonts(Control control, Dictionary<Control, Font> fonts)
    {
        fonts.Add(control, control.Font);
        foreach (Control child in control.Controls) CaptureFonts(child, fonts);
    }

    private static int PreviewPixels(int value) { return (int)Math.Round(value * PreviewScale); }

    private void UpdateResponsiveLayout()
    {
        if (!responsiveLayoutReady || updatingResponsiveLayout || rootLayout == null || IsDisposed) return;
        updatingResponsiveLayout = true;
        try
        {
            // A task-sized column preserves readable lines on wide displays. Extra height
            // surrounds the complete content group instead of inflating the status row.
            int contentWidth = Math.Min(ClientSize.Width, PreviewPixels(MaximumContentWidth));
            int linksWidth = 0;
            foreach (Control link in headerLinks.Controls) linksWidth += link.Width + link.Margin.Horizontal;
            int brandWidth = PreviewPixels(146);
            int domainWidth = PreviewPixels(70);
            int buttonsWidth = PreviewPixels(88 + 6 + 100);
            int noteWidth = MeasureTextWidth(restartNote) + PreviewPixels(10);
            int contentHeight = 0;
            bool needsScroll = false;
            rootLayout.SuspendLayout();
            // Calculate again with the scrollbar's width when needed, so wrapping
            // and breakpoints use the space actually available to the controls.
            for (int pass = 0; pass < 2; pass++)
            {
                int innerWidth = Math.Max(1, contentWidth - rootLayout.Padding.Horizontal);
                rootLayout.RowStyles[1].Height = accountPanel.RequiredRowHeight(innerWidth);
                compactHeader = innerWidth < brandWidth + domainWidth + linksWidth + PreviewPixels(12);
                int connectionWidth = innerWidth - connectionPanel.Padding.Horizontal;
                compactActions = connectionWidth < Math.Max(noteWidth + buttonsWidth, PreviewPixels(460));
                int actionsHeight = PreviewPixels(compactActions ? 54 : 30);
                int hintHeight = Math.Max(PreviewPixels(28), MeasureLabelHeight(credentialHintLabel, connectionWidth));
                int statusHeight = Math.Max(PreviewPixels(32), MeasureLabelHeight(statusLabel, innerWidth - PreviewPixels(4)) + PreviewPixels(4));
                connectionLayout.RowStyles[2].Height = hintHeight;
                connectionLayout.RowStyles[3].Height = actionsHeight;
                rootLayout.RowStyles[0].Height = PreviewPixels(compactHeader ? 74 : 42);
                rootLayout.RowStyles[ConnectionRow].Height = connectionPanel.Margin.Vertical + connectionPanel.Padding.Vertical
                    + PreviewPixels(22 + 36) + hintHeight + actionsHeight;
                rootLayout.RowStyles[StatusRow].Height = statusHeight;
                contentHeight = rootLayout.Padding.Vertical;
                foreach (RowStyle row in rootLayout.RowStyles) contentHeight += (int)Math.Ceiling(row.Height);
                needsScroll = contentHeight > ClientSize.Height;
                int nextWidth = Math.Min(PreviewPixels(MaximumContentWidth), ClientSize.Width - (needsScroll ? SystemInformation.VerticalScrollBarWidth : 0));
                if (nextWidth == contentWidth) break;
                contentWidth = nextWidth;
            }
            layoutViewport.SuspendLayout();
            try
            {
                // Reset the old scroll offset before centering a now-fitting group.
                // Otherwise WinForms includes that offset in the virtual extent and
                // can retain both scrollbars after a short window is made taller.
                if (!needsScroll) layoutViewport.AutoScrollPosition = Point.Empty;
                layoutViewport.AutoScrollMinSize = new Size(0, contentHeight);
                int availableWidth = ClientSize.Width - (needsScroll ? SystemInformation.VerticalScrollBarWidth : 0);
                int x = Math.Max(0, (availableWidth - contentWidth) / 2);
                int y = needsScroll ? layoutViewport.AutoScrollPosition.Y : Math.Max(0, (ClientSize.Height - contentHeight) / 2);
                rootLayout.SetBounds(x, y, contentWidth, contentHeight);
                rootLayout.ResumeLayout(true);
            }
            finally { layoutViewport.ResumeLayout(true); }

            int headerY = PreviewPixels(5);
            brandLabel.SetBounds(0, headerY, brandWidth, PreviewPixels(28));
            endpointAddress.SetBounds(brandWidth, headerY, domainWidth, PreviewPixels(28));
            headerLinks.SetBounds(compactHeader ? 0 : Math.Max(0, headerPanel.Width - linksWidth),
                compactHeader ? PreviewPixels(37) : headerY, linksWidth, PreviewPixels(28));

            int actionWidth = connectionActions.ClientSize.Width;
            int buttonY = compactActions ? PreviewPixels(24) : 0;
            int buttonHeight = PreviewPixels(30);
            restartNote.SetBounds(0, 0, compactActions ? actionWidth : Math.Max(0, actionWidth - buttonsWidth - PreviewPixels(10)),
                compactActions ? PreviewPixels(24) : buttonHeight);
            openCodexButton.SetBounds(Math.Max(0, actionWidth - buttonsWidth), buttonY, PreviewPixels(88), buttonHeight);
            restoreButton.SetBounds(Math.Max(0, actionWidth - PreviewPixels(100)), buttonY, PreviewPixels(100), buttonHeight);
            if (layoutCredentialOverlays != null) layoutCredentialOverlays();
        }
        finally { updatingResponsiveLayout = false; }
    }

    private static int MeasureTextWidth(Label label)
    {
        using (Graphics graphics = label.CreateGraphics())
            return (int)Math.Ceiling(graphics.MeasureString(label.Text, label.Font).Width);
    }

    private static int MeasureLabelHeight(Label label, int width)
    {
        using (Graphics graphics = label.CreateGraphics())
            return (int)Math.Ceiling(graphics.MeasureString(String.IsNullOrEmpty(label.Text) ? " " : label.Text,
                label.Font, Math.Max(1, width - label.Padding.Horizontal)).Height) + label.Padding.Vertical;
    }

    private Control BuildHeader()
    {
        headerPanel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = pageBack };
        brandLabel = CreateLabel("codex官方中转站", 13, accent);
        brandLabel.Name = "brandLabel";
        brandLabel.Font = new Font(Font.FontFamily, 13, FontStyle.Bold, GraphicsUnit.Pixel);
        endpointAddress = CreateLabel(new Uri(AppController.OfficialRelayUrl).Host, 11, mutedColor);
        endpointAddress.Font = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
        headerPanel.Controls.Add(endpointAddress);
        headerPanel.Controls.Add(brandLabel);
        headerLinks = new FlowLayoutPanel { Width = 344, Margin = Padding.Empty, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        officialWebsiteButton = CreateSecondaryButton("官网 ↗");
        officialWebsiteButton.Size = new Size(60, 28);
        officialWebsiteButton.Dock = DockStyle.None;
        officialWebsiteButton.Margin = new Padding(4, 0, 0, 0);
        officialWebsiteButton.Click += OpenOfficialWebsite;
        headerLinks.Controls.Add(officialWebsiteButton);
        updateButton = CreateSecondaryButton("检查更新");
        updateButton.Size = new Size(98, 28);
        updateButton.Dock = DockStyle.None;
        updateButton.Margin = new Padding(4, 0, 0, 0);
        updateButton.Click += async delegate
        {
            if (pendingUpdate == null) await CheckUpdatesAsync(true);
            else RestartAndUpdate();
        };
        updateButton.Enabled = !previewMode;
        headerLinks.Controls.Add(updateButton);
        appearanceButton = CreateSecondaryButton("主题选择");
        appearanceButton.AccessibleName = "打开 Codex 主题选择";
        appearanceButton.Size = new Size(90, 28);
        appearanceButton.Dock = DockStyle.None;
        appearanceButton.Margin = Padding.Empty;
        appearanceButton.Click += OpenAppearanceSettings;
        headerLinks.Controls.Add(appearanceButton);
        foreach (DesktopButton button in headerLinks.Controls)
        {
            button.Borderless = true;
            button.BackColor = pageBack;
            button.ForeColor = mutedColor;
        }
        appearanceButton.BackColor = Color.FromArgb(237, 244, 255);
        appearanceButton.ForeColor = accent;
        headerPanel.Controls.Add(headerLinks);
        return headerPanel;
    }

    private Control BuildConnectionSection()
    {
        connectionPanel = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 8),
            BackColor = panelBack, BorderColor = borderColor, CornerRadius = 8, Padding = new Padding(14, 12, 14, 12) };
        connectionLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        var layout = connectionLayout;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        var keyLabel = CreateLabel("mid-web API Key", 12, textColor);
        keyLabel.Dock = DockStyle.Fill;
        layout.Controls.Add(keyLabel, 0, 0);
        apiKeyTextBox = new CredentialTextBox { HintColor = mutedColor, UseSystemPasswordChar = true, BorderStyle = BorderStyle.None,
            BackColor = inputBack, ForeColor = textColor, Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel), AccessibleName = "mid-web API Key" };
        var keyFrame = new DesktopSurface { Dock = DockStyle.Fill, BackColor = inputBack,
            BorderColor = borderColor, CornerRadius = 6, Padding = new Padding(10, 8, 48, 5), Margin = Padding.Empty };
        apiKeyTextBox.Enter += delegate { keyFrame.BorderColor = accentDark; keyFrame.Invalidate(); };
        apiKeyTextBox.Leave += delegate { keyFrame.BorderColor = borderColor; keyFrame.Invalidate(); };
        keyFrame.Controls.Add(apiKeyTextBox);
        eyeButton = CreateSecondaryButton("显示");
        eyeButton.AccessibleName = "显示或隐藏 API Key";
        eyeButton.Click += ToggleApiKeyVisible;
        ((DesktopButton)eyeButton).Borderless = true;
        eyeButton.Dock = DockStyle.None;
        keyFrame.Controls.Add(eyeButton);
        layoutCredentialOverlays = delegate {
            eyeButton.SetBounds(keyFrame.Width - PreviewPixels(45), 3, PreviewPixels(44) - 2, keyFrame.Height - 6);
        };
        keyFrame.Layout += delegate { layoutCredentialOverlays(); };
        keyFrame.Paint += delegate(object sender, PaintEventArgs e) {
            using (var pen = new Pen(borderColor)) e.Graphics.DrawLine(pen, keyFrame.Width - PreviewPixels(46), 1, keyFrame.Width - PreviewPixels(46), keyFrame.Height - 2);
        };
        applyButton = CreatePrimaryButton("配置并启动");
        applyButton.Click += async delegate { await ApplyAsync(); };
        var entry = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 3, RowCount = 1 };
        entry.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        entry.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
        entry.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        entry.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        entry.Controls.Add(keyFrame, 0, 0);
        entry.Controls.Add(applyButton, 2, 0);
        layout.Controls.Add(entry, 0, 1);
        credentialHintLabel = CreateLabel("", 11, mutedColor);
        credentialHintLabel.Dock = DockStyle.Fill;
        credentialHintLabel.TextAlign = ContentAlignment.TopLeft;
        credentialHintLabel.Padding = new Padding(0, 6, 0, 0);
        layout.Controls.Add(credentialHintLabel, 0, 2);
        connectionActions = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        restartNote = CreateLabel("保存设置后重新启动 Codex", 11, mutedColor);
        openCodexButton = CreateSecondaryButton("打开 Codex");
        restoreButton = CreateSecondaryButton("恢复上次配置");
        openCodexButton.Dock = DockStyle.None;
        restoreButton.Dock = DockStyle.None;
        openCodexButton.Click += OpenCodex;
        restoreButton.Click += Restore;
        connectionActions.Controls.Add(restartNote);
        connectionActions.Controls.Add(openCodexButton);
        connectionActions.Controls.Add(restoreButton);
        layout.Controls.Add(connectionActions, 0, 3);
        connectionPanel.Controls.Add(layout);
        return connectionPanel;
    }

    private Control BuildStatusBar()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(2, 4, 2, 0) };
        statusLabel = CreateLabel("准备就绪。", 11, mutedColor);
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.TextAlign = ContentAlignment.TopLeft;
        statusLabel.Padding = new Padding(0, 4, 0, 0);
        statusLabel.AutoEllipsis = true;
        statusLabel.AccessibleName = "操作状态";
        panel.Controls.Add(statusLabel);
        return panel;
    }

    private Button CreatePrimaryButton(string text)
    {
        return new DesktopButton { Text = text, Primary = true, Dock = DockStyle.Fill, Margin = Padding.Empty,
            AccentColor = accent, BorderColor = borderColor, CornerRadius = 6, Font = new Font(Font.FontFamily, 12, FontStyle.Regular, GraphicsUnit.Pixel) };
    }

    private Button CreateSecondaryButton(string text)
    {
        return new DesktopButton { Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty,
            CornerRadius = 6, BorderColor = borderColor, AccentColor = accent, ForeColor = textColor,
            Font = new Font(Font.FontFamily, 12, FontStyle.Regular, GraphicsUnit.Pixel) };
    }
    private void LoadInitialState()
    {
        if (previewMode)
        {
            SetCredentialHint(false);
            SetOperationsEnabled(false);
            SetStatus("预览模式：操作已禁用，不读取真实密钥或写入配置。");
            return;
        }

        SetCredentialHint(controller.HasCredential);
        SetStatus("已加载本机配置状态。");
    }

    private void SetOperationsEnabled(bool enabled)
    {
        applyButton.Enabled = enabled;
        openCodexButton.Enabled = enabled;
        restoreButton.Enabled = enabled;
        appearanceButton.Enabled = true;
        updateButton.Enabled = enabled;
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (checkingUpdate || previewMode || IsDisposed) return;
        checkingUpdate = true;
        updateButton.Enabled = false;
        updateButton.Text = "检查中...";
        try
        {
            PendingUpdate update = await AutoUpdater.CheckAndStageAsync();
            if (IsDisposed) return;
            pendingUpdate = update;
            updateButton.Text = update == null ? "检查更新" : "重启并更新";
            if (update != null) SetStatus("新版 " + update.Version + " 已下载并校验，点击“重启并更新”安装。");
            else if (manual) SetStatus("当前已是最新版本（" + AutoUpdater.CurrentVersion.ToString(3) + "）。");
        }
        catch (Exception error)
        {
            if (IsDisposed) return;
            updateButton.Text = "检查更新";
            if (manual) SetStatus("检查更新失败：" + error.Message);
            Debug.WriteLine("Update check failed: " + error);
        }
        finally
        {
            checkingUpdate = false;
            if (!IsDisposed) updateButton.Enabled = true;
        }
    }

    private void RestartAndUpdate()
    {
        try
        {
            AutoUpdater.LaunchInstaller(pendingUpdate);
            Close();
        }
        catch (Exception error) { SetStatus("启动更新失败：" + error.Message); }
    }

    private void SetCredentialHint(bool hasCredential)
    {
        credentialHintLabel.Text = "留空保留已有密钥 · 本机加密保存副本；Codex 配置文件仍保存令牌";
    }

    private async Task ApplyAsync()
    {
        UserSettings settings = new UserSettings();
        settings.BaseUrl = AppController.OfficialRelayUrl;
        settings.Model = AppController.RelayModel;

        string newApiKey = apiKeyTextBox.Text;
        if (newApiKey.Trim().Length == 0)
        {
            SetStatus(controller.HasCredential ? "ApiKey 留空，将保留已有加密密钥。" : "未存 ApiKey，请输入新密钥后再配置。");
            if (!controller.HasCredential)
            {
                return;
            }
        }

        SetBusy(true, "正在退出 Codex、保存配置并重新启动；如出现官方退出确认，请确认...");
        try
        {
            ConfigurationLaunchResult result = await ConfigurationLaunchWorkflow.RunAsync(
                () => controller.ApplyAsync(settings, newApiKey), () => controller.OpenCodex());
            apiKeyTextBox.Clear();
            SetCredentialHint(true);
            SetStatus(result.Message);
        }
        catch (Exception ex)
        {
            SetStatus("配置失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async void OpenCodex(object sender, EventArgs e)
    {
        SetBusy(true, "正在打开 Codex...");
        try
        {
            await Task.Run(() => controller.OpenCodex());
            SetStatus("已打开 Codex。新建任务使用中转。");
        }
        catch (Exception ex)
        {
            SetStatus("打开 Codex 失败：" + ex.Message);
        }
        finally { SetBusy(false, null); }
    }

    private async void Restore(object sender, EventArgs e)
    {
        SetBusy(true, "正在请求 Codex 正常退出；如出现退出确认，请确认后等待备份检查...");
        try
        {
            RestorePlan plan = await Task.Run(() => controller.PrepareRestore());
            bool confirmed = false;
            if (plan.ConflictingFiles.Length > 0)
            {
                confirmed = MessageBox.Show(this,
                    "以下文件在上次配置后发生了变化：\r\n" + String.Join("\r\n", plan.ConflictingFiles) +
                    "\r\n\r\n继续将先加密备份当前内容，再将这些文件还原到上次一键配置之前。之后更改的登录信息和配置也会被替换。\r\n是否继续恢复？",
                    "确认恢复上次配置", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                if (!confirmed) { SetStatus("已取消恢复，配置文件未修改。"); return; }
            }
            string result = await Task.Run(() => controller.CompleteRestore(plan, confirmed));
            apiKeyTextBox.Clear();
            LoadInitialState();
            SetStatus(EmptyTo(result, "已请求恢复上次配置。"));
        }
        catch (Exception ex)
        {
            SetStatus("恢复失败：" + ex.Message);
        }
        finally { SetBusy(false, null); }
    }

    private void OpenAppearanceSettings(object sender, EventArgs e)
    {
        try
        {
            using (ThemeGalleryForm form = new ThemeGalleryForm(controller, themeGalleryService, previewMode))
            {
                form.ShowDialog(this);
                if (form.AppearanceUpdated)
                    SetStatus("外观设置已保存并应用。");
                else if (!String.IsNullOrWhiteSpace(form.AppliedThemeId))
                    SetStatus("已应用主题：" + form.AppliedThemeId);
            }
        }
        catch (Exception ex)
        {
            SetStatus("打开外观设置失败：" + ex.Message);
        }
    }

    private void OpenOfficialWebsite(object sender, EventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppController.OfficialWebsiteUrl) { UseShellExecute = true });
            SetStatus("已在浏览器打开官方网站。");
        }
        catch (Exception ex)
        {
            SetStatus("打开官方网站失败：" + ex.Message);
        }
    }


    private async Task LoadAdvertisementsAsync()
    {
        if (loadingAdvertisements || IsDisposed || advertisementCancellation.IsCancellationRequested) return;
        string statusBeforeRequest = statusLabel.Text;
        loadingAdvertisements = true;
        try
        {
            Advertisement[] items = await new AdvertisementClient().FetchAsync(advertisementCancellation.Token);
            if (IsDisposed || advertisementCancellation.IsCancellationRequested) return;
            SetAdvertisements(items);
            if (statusLabel.Text == "推广信息暂时无法加载，稍后自动重试。") SetStatus("准备就绪。");
        }
        catch (Exception error)
        {
            if (IsDisposed || advertisementCancellation.IsCancellationRequested) return;
            SetAdvertisements(new Advertisement[0]);
            if (applyButton.Enabled && statusLabel.Text == statusBeforeRequest)
                SetStatus("推广信息暂时无法加载，稍后自动重试。");
            Debug.WriteLine("Advertisement refresh failed: " + error.GetType().Name);
        }
        finally { loadingAdvertisements = false; }
    }

    internal void SetAdvertisements(Advertisement[] items)
    {
        int height = items.Length == 0 ? 0 : PreviewPixels(50);
        rootLayout.SuspendLayout();
        advertisementBanner.SetItems(items);
        rootLayout.RowStyles[AdvertisementRow].Height = height;
        rootLayout.AutoScrollMinSize = Size.Empty;
        AdjustAdvertisementWindowHeight();
        rootLayout.ResumeLayout(true);
        UpdateResponsiveLayout();
    }

    private void AdjustAdvertisementWindowHeight()
    {
        int height = (int)rootLayout.RowStyles[AdvertisementRow].Height;
        if (WindowState != FormWindowState.Normal || height == advertisementWindowRowHeight) return;
        int availableHeight = Screen.FromControl(this).WorkingArea.Height - (Height - ClientSize.Height);
        int baseHeight = ClientSize.Height - advertisementWindowHeightAdjustment;
        int nextHeight = height == 0 ? Math.Max(PreviewPixels(BaseClientHeight), baseHeight)
            : Math.Max(ClientSize.Height, Math.Min(availableHeight, baseHeight + height));
        advertisementWindowRowHeight = height;
        advertisementWindowHeightAdjustment = height == 0 ? 0 : nextHeight - baseHeight;
        ClientSize = new Size(ClientSize.Width, nextHeight);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateResponsiveLayout();
        if (rootLayout != null && rootLayout.RowStyles.Count > AdvertisementRow && IsHandleCreated &&
            (int)rootLayout.RowStyles[AdvertisementRow].Height != advertisementWindowRowHeight)
        {
            // WinForms restores its saved bounds after the resize notification.
            BeginInvoke((Action)delegate { if (!IsDisposed) AdjustAdvertisementWindowHeight(); });
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            advertisementRefreshTimer.Dispose();
            advertisementCancellation.Cancel();
        }
        base.Dispose(disposing);
    }

    private void ToggleApiKeyVisible(object sender, EventArgs e)
    {
        showingApiKey = !showingApiKey;
        apiKeyTextBox.UseSystemPasswordChar = !showingApiKey;
        eyeButton.Text = showingApiKey ? "隐藏" : "显示";
    }

    private void SetBusy(bool busy, string message)
    {
        accountPanel.SetOperationsEnabled(!busy);
        applyButton.Enabled = !busy && !previewMode;
        openCodexButton.Enabled = !busy && !previewMode;
        restoreButton.Enabled = !busy && !previewMode;
        appearanceButton.Enabled = !busy;
        officialWebsiteButton.Enabled = !busy;
        updateButton.Enabled = !busy && !previewMode && !checkingUpdate;
        if (!String.IsNullOrEmpty(message))
        {
            SetStatus(message);
        }
    }

    private void SetStatus(string text)
    {
        statusLabel.Text = text;
        UpdateResponsiveLayout();
    }

    private string EmptyTo(string value, string fallback)
    {
        return String.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private sealed class CredentialTextBox : TextBox
    {
        internal Color HintColor;

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            // Password edit controls do not support the native cue banner.
            if ((message.Msg != 0x000F && message.Msg != 0x0318) || TextLength != 0) return;
            using (Graphics graphics = message.Msg == 0x0318 && message.WParam != IntPtr.Zero
                ? Graphics.FromHdc(message.WParam) : Graphics.FromHwnd(Handle))
                DesktopPalette.DrawText(graphics, "输入 API Key", Font, ClientRectangle, HintColor, StringAlignment.Near);
        }
    }

    private class RoundedPanel : DesktopSurface
    {
    }

    private class SkinChoice
    {
        public string Id { get; private set; }
        public string Label { get; private set; }

        public SkinChoice(string id, string label)
        {
            Id = id;
            Label = label;
        }

        public override string ToString()
        {
            return Label;
        }
    }
}
