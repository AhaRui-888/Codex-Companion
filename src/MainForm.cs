using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

public class MainForm : Form
{
    private readonly AppController controller;
    private readonly bool previewMode;
    private readonly IThemeGalleryService themeGalleryService;
    // Presentation values only; relay and credential behavior remains in AppController.
    private readonly Color accent = DesktopPalette.Accent;
    private readonly Color accentDark = DesktopPalette.Accent;
    private readonly Color pageBack = DesktopPalette.Page;
    private readonly Color panelBack = DesktopPalette.Surface;
    private readonly Color inputBack = DesktopPalette.Input;
    private readonly Color textColor = DesktopPalette.Text;
    private readonly Color mutedColor = DesktopPalette.Muted;
    private readonly Color borderColor = DesktopPalette.Border;
    private const int BaseClientHeight = 710;

    private TextBox apiKeyTextBox;
    private ComboBox themeComboBox;
    private ComboBox skinComboBox;
    private ComboBox petComboBox;
    private Label statusLabel;
    private Label credentialHintLabel;
    private Panel themePreviewPanel;
    private Button applyButton;
    private Button openCodexButton;
    private Button restoreButton;
    private Button galleryButton;
    private Button officialWebsiteButton;
    private Button rechargeButton;
    private Button updateButton;
    private Button eyeButton;
    private TableLayoutPanel rootLayout;
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
        BindChoices();
        LoadInitialState();
        if (!previewMode)
        {
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
        Text = "Codex Companion";
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Regular, GraphicsUnit.Pixel);
        BackColor = pageBack;
        ForeColor = textColor;
        ClientSize = new Size(960, BaseClientHeight);
        MinimumSize = new Size(876, BaseClientHeight + 39);
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
        rootLayout = new TableLayoutPanel {
            Dock = DockStyle.Fill, BackColor = pageBack, Padding = new Padding(30, 22, 30, 16),
            ColumnCount = 1, RowCount = 6, AutoScroll = true, Margin = Padding.Empty
        };
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 264));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(rootLayout);
        rootLayout.Controls.Add(BuildHeader(), 0, 0);
        advertisementBanner = new AdvertisementBanner();
        rootLayout.Controls.Add(advertisementBanner, 0, 1);

        rootLayout.Controls.Add(BuildProviderSection(), 0, 2);
        var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        workspace.Controls.Add(BuildConnectionSection(), 0, 0);
        workspace.Controls.Add(BuildAppearanceSection(), 2, 0);
        rootLayout.Controls.Add(workspace, 0, 3);
        rootLayout.Controls.Add(BuildActionsSection(), 0, 4);
        rootLayout.Controls.Add(BuildStatusBar(), 0, 5);
    }

    private Label CreateLabel(string text, float size, Color color)
    {
        return new Label { Text = text, ForeColor = color, BackColor = Color.Transparent,
            Font = new Font(Font.FontFamily, size, FontStyle.Regular, GraphicsUnit.Pixel),
            Margin = Padding.Empty, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = pageBack };
        var brand = CreateLabel("Codex Companion", 19, accent);
        brand.Name = "brandLabel";
        brand.Font = new Font("Segoe UI", 19, FontStyle.Bold, GraphicsUnit.Pixel);
        var topbar = new Panel { Dock = DockStyle.Top, Height = 42, Margin = Padding.Empty };
        brand.Dock = DockStyle.Left;
        brand.Width = 230;
        var versionLabel = CreateLabel("v" + AutoUpdater.CurrentVersion.ToString(3), 11, mutedColor);
        versionLabel.Dock = DockStyle.Left;
        versionLabel.Width = 72;
        topbar.Controls.Add(versionLabel);
        topbar.Controls.Add(brand);
        var links = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 388, Margin = Padding.Empty, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        officialWebsiteButton = CreateSecondaryButton("官方网站 ↗");
        officialWebsiteButton.Size = new Size(118, 32);
        officialWebsiteButton.Dock = DockStyle.None;
        officialWebsiteButton.Margin = new Padding(8, 0, 0, 0);
        officialWebsiteButton.Click += OpenOfficialWebsite;
        rechargeButton = CreateSecondaryButton("充值中心");
        rechargeButton.Size = new Size(118, 32);
        rechargeButton.Dock = DockStyle.None;
        rechargeButton.Margin = new Padding(8, 0, 0, 0);
        rechargeButton.Click += OpenRecharge;
        links.Controls.Add(officialWebsiteButton);
        links.Controls.Add(rechargeButton);
        updateButton = CreateSecondaryButton("检查更新");
        updateButton.Size = new Size(118, 32);
        updateButton.Dock = DockStyle.None;
        updateButton.Margin = new Padding(8, 0, 0, 0);
        updateButton.Click += async delegate
        {
            if (pendingUpdate == null) await CheckUpdatesAsync(true);
            else RestartAndUpdate();
        };
        updateButton.Enabled = !previewMode;
        links.Controls.Add(updateButton);
        topbar.Controls.Add(links);
        panel.Controls.Add(topbar);

        var title = CreateLabel("连接与外观", 22, textColor);
        title.Font = new Font(Font.FontFamily, 22, FontStyle.Bold, GraphicsUnit.Pixel);
        title.SetBounds(0, 54, 400, 34);
        panel.Controls.Add(title);
        var subtitle = CreateLabel("管理中转凭据及 Codex 界面设置", 12, mutedColor);
        subtitle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        subtitle.TextAlign = ContentAlignment.MiddleRight;
        subtitle.SetBounds(520, 60, 380, 24);
        panel.Controls.Add(subtitle);
        panel.Resize += delegate { subtitle.Left = panel.ClientSize.Width - subtitle.Width; };
        return panel;
    }

    private Control BuildProviderSection()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = pageBack, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 10) };
        var heading = CreateLabel("连接服务", 15, textColor);
        heading.Font = new Font(Font.FontFamily, 15, FontStyle.Bold, GraphicsUnit.Pixel);
        heading.Dock = DockStyle.Top;
        heading.Height = 30;
        var endpoint = new DesktopSurface { Dock = DockStyle.Fill, BackColor = DesktopPalette.Selected,
            BorderColor = Color.FromArgb(130, 183, 250), CornerRadius = 16, Margin = Padding.Empty };
        endpoint.Name = "providerSection";
        var badge = CreateLabel("C", 18, accent);
        badge.Font = new Font("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel);
        badge.TextAlign = ContentAlignment.MiddleCenter;
        badge.Dock = DockStyle.Fill;
        var badgeFrame = new DesktopSurface { BackColor = Color.White, BorderColor = Color.FromArgb(221, 232, 247), CornerRadius = 12 };
        badgeFrame.SetBounds(22, 18, 44, 44);
        badgeFrame.Controls.Add(badge);
        var endpointTitle = CreateLabel("Codex Relay 官方中转", 17, textColor);
        endpointTitle.Font = new Font(Font.FontFamily, 17, FontStyle.Bold, GraphicsUnit.Pixel);
        endpointTitle.SetBounds(82, 14, 350, 28);
        var endpointAddress = CreateLabel(new Uri(AppController.OfficialRelayUrl).Host, 14, accent);
        endpointAddress.Font = new Font("Segoe UI", 14, FontStyle.Regular, GraphicsUnit.Pixel);
        endpointAddress.SetBounds(82, 43, 350, 25);
        var target = CreateLabel("配置目标", 12, accent);
        target.TextAlign = ContentAlignment.MiddleRight;
        target.SetBounds(730, 27, 120, 25);
        endpoint.Resize += delegate { target.Left = endpoint.ClientSize.Width - target.Width - 24; };
        endpoint.Controls.Add(target);
        endpoint.Controls.Add(endpointAddress);
        endpoint.Controls.Add(endpointTitle);
        endpoint.Controls.Add(badgeFrame);
        panel.Controls.Add(endpoint);
        panel.Controls.Add(heading);
        return panel;
    }

    private Control BuildConnectionSection()
    {
        RoundedPanel panel = CreateSectionPanel("访问凭据", "API Key");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        var keyLabel = CreateLabel("API Key", 13, mutedColor);
        keyLabel.Dock = DockStyle.Fill;
        layout.Controls.Add(keyLabel, 0, 0);
        apiKeyTextBox = new TextBox { UseSystemPasswordChar = true, BorderStyle = BorderStyle.None,
            BackColor = inputBack, ForeColor = textColor, Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel), AccessibleName = "API Key" };
        var keyFrame = new DesktopSurface { Dock = DockStyle.Fill, BackColor = inputBack,
            CornerRadius = 10, Padding = new Padding(12, 11, 10, 7), Margin = Padding.Empty };
        apiKeyTextBox.Enter += delegate { keyFrame.BorderColor = accentDark; keyFrame.Invalidate(); };
        apiKeyTextBox.Leave += delegate { keyFrame.BorderColor = borderColor; keyFrame.Invalidate(); };
        keyFrame.Controls.Add(apiKeyTextBox);
        eyeButton = CreateSecondaryButton("显示");
        eyeButton.AccessibleName = "显示或隐藏 API Key";
        eyeButton.Click += ToggleApiKeyVisible;
        layout.Controls.Add(Combine(keyFrame, eyeButton, 58), 0, 1);
        credentialHintLabel = CreateLabel("", 12, mutedColor);
        credentialHintLabel.Dock = DockStyle.Fill;
        credentialHintLabel.TextAlign = ContentAlignment.TopLeft;
        credentialHintLabel.Padding = new Padding(0, 10, 0, 0);
        layout.Controls.Add(credentialHintLabel, 0, 2);
        panel.Body.Controls.Add(layout);
        return panel;
    }

    private Control BuildAppearanceSection()
    {
        RoundedPanel panel = CreateSectionPanel("外观偏好", "Codex 桌面界面");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        TableLayoutPanel grid = CreateGrid(3);
        themeComboBox = CreateComboBox();
        themeComboBox.AccessibleName = "Codex 主题";
        themeComboBox.SelectedIndexChanged += delegate { UpdateThemePreview(); };
        skinComboBox = CreateComboBox();
        skinComboBox.AccessibleName = "Codex 皮肤";
        petComboBox = CreateComboBox();
        petComboBox.AccessibleName = "Codex 宠物";
        AddRow(grid, 0, "主题", themeComboBox);
        AddRow(grid, 1, "皮肤", skinComboBox);
        AddRow(grid, 2, "宠物", petComboBox);
        layout.Controls.Add(grid, 0, 0);
        themePreviewPanel = new DesktopSurface { Dock = DockStyle.Fill, BackColor = pageBack,
            BorderColor = pageBack, CornerRadius = 8, Margin = new Padding(0, 6, 0, 0), AccessibleName = "当前主题配色示意" };
        themePreviewPanel.Paint += PaintThemePreview;
        themePreviewPanel.Resize += delegate { themePreviewPanel.Invalidate(); };
        galleryButton = CreateSecondaryButton("主题图库 ↗");
        galleryButton.Size = new Size(132, 34);
        galleryButton.Dock = DockStyle.None;
        galleryButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        galleryButton.Click += OpenSkinGallery;
        themePreviewPanel.Controls.Add(galleryButton);
        themePreviewPanel.Resize += delegate { galleryButton.Location = new Point(themePreviewPanel.Width - 144, themePreviewPanel.Height - 46); };
        layout.Controls.Add(themePreviewPanel, 0, 1);
        panel.Body.Controls.Add(layout);
        return panel;
    }

    private Control BuildActionsSection()
    {
        var panel = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 8),
            BackColor = panelBack, BorderColor = borderColor, CornerRadius = 14, Padding = new Padding(18, 14, 18, 14) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 204));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 10));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = CreateLabel("应用配置", 16, textColor);
        heading.Font = new Font(Font.FontFamily, 16, FontStyle.Bold, GraphicsUnit.Pixel);
        heading.Dock = DockStyle.Fill;
        layout.Controls.Add(heading, 0, 0);
        var note = CreateLabel("保存设置后重新启动 Codex", 11, mutedColor);
        note.Dock = DockStyle.Fill;
        layout.Controls.Add(note, 0, 1);
        applyButton = CreatePrimaryButton("配置并启动");
        openCodexButton = CreateSecondaryButton("打开 Codex");
        restoreButton = CreateSecondaryButton("恢复上次配置");
        restoreButton.Margin = new Padding(8, 0, 0, 0);
        applyButton.Click += async delegate { await ApplyAsync(); };
        openCodexButton.Click += OpenCodex;
        restoreButton.Click += Restore;
        layout.Controls.Add(applyButton, 1, 0);
        layout.Controls.Add(openCodexButton, 3, 0);
        layout.Controls.Add(restoreButton, 4, 0);
        var actionHint = CreateLabel("API Key 仅在本机加密保存", 11, mutedColor);
        actionHint.TextAlign = ContentAlignment.MiddleRight;
        actionHint.Dock = DockStyle.Fill;
        layout.Controls.Add(actionHint, 1, 1);
        layout.SetColumnSpan(actionHint, 4);
        panel.Controls.Add(layout);
        return panel;
    }

    private Control BuildStatusBar()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(2, 4, 2, 0) };
        var label = CreateLabel("状态", 11, mutedColor);
        label.Dock = DockStyle.Top;
        label.Height = 19;
        statusLabel = CreateLabel("准备就绪。", 12, textColor);
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.TextAlign = ContentAlignment.TopLeft;
        statusLabel.Padding = new Padding(0, 4, 0, 0);
        statusLabel.AutoEllipsis = true;
        statusLabel.AccessibleName = "操作状态";
        panel.Controls.Add(statusLabel);
        panel.Controls.Add(label);
        return panel;
    }

    private RoundedPanel CreateSectionPanel(string titleText, string caption)
    {
        var panel = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 0),
            BackColor = pageBack, BorderColor = pageBack, CornerRadius = 8, Padding = new Padding(0, 4, 0, 0) };
        var header = new Panel { Dock = DockStyle.Top, Height = 40 };
        var title = CreateLabel(titleText, 18, textColor);
        title.Dock = DockStyle.Left;
        title.Width = 85;
        var number = CreateLabel(caption, 11, mutedColor);
        number.Font = new Font(Font.FontFamily, 11, FontStyle.Regular, GraphicsUnit.Pixel);
        number.TextAlign = ContentAlignment.MiddleRight;
        number.Dock = DockStyle.Fill;
        header.Controls.Add(number);
        header.Controls.Add(title);
        panel.Body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0), BackColor = pageBack };
        panel.Controls.Add(panel.Body);
        panel.Controls.Add(header);
        return panel;
    }

    private TableLayoutPanel CreateGrid(int rows)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = rows, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < rows; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / rows));
        return grid;
    }

    private void AddRow(TableLayoutPanel grid, int row, string labelText, Control input)
    {
        var label = CreateLabel(labelText, 13, mutedColor);
        label.Dock = DockStyle.Fill;
        input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        input.Margin = Padding.Empty;
        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(input, 1, row);
    }

    private Control Combine(Control fill, Control right, int rightWidth)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 3, RowCount = 1 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, rightWidth));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        fill.Dock = right.Dock = DockStyle.Fill;
        fill.Margin = right.Margin = Padding.Empty;
        panel.Controls.Add(fill, 0, 0);
        panel.Controls.Add(right, 2, 0);
        return panel;
    }

    private ComboBox CreateComboBox()
    {
        return new DesktopComboBox { Font = new Font(Font.FontFamily, 13, FontStyle.Regular, GraphicsUnit.Pixel) };
    }

    private Button CreatePrimaryButton(string text)
    {
        return new DesktopButton { Text = text, Primary = true, Dock = DockStyle.Fill, Margin = Padding.Empty,
            Font = new Font(Font.FontFamily, 13, FontStyle.Bold, GraphicsUnit.Pixel) };
    }

    private Button CreateSecondaryButton(string text)
    {
        return new DesktopButton { Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty,
            Font = new Font(Font.FontFamily, 12, FontStyle.Regular, GraphicsUnit.Pixel) };
    }
    private void BindChoices()
    {
        BindChoiceCombo(themeComboBox, ThemeCatalog.Themes);
        BindChoiceCombo(skinComboBox, new Choice[] {
            new Choice("native", "Codex 原生界面", null, null, null, null, null),
            new Choice("qq", "QQ 风格 · 蓝银面板", null, null, null, null, null)
        });
        BindChoiceCombo(petComboBox, ThemeCatalog.Pets);
    }

    private void BindChoiceCombo(ComboBox comboBox, object[] values)
    {
        comboBox.DisplayMember = "Label";
        comboBox.ValueMember = "Id";
        comboBox.DataSource = values;
    }

    private void LoadInitialState()
    {
        if (previewMode)
        {
            SelectValue(themeComboBox, "keep");
            SelectValue(skinComboBox, "native");
            SelectValue(petComboBox, "keep");
            SetCredentialHint(false);
            SetOperationsEnabled(false);
            SetStatus("预览模式：操作已禁用，不读取真实密钥或写入配置。");
            return;
        }

        UserSettings settings = controller.Load();
        if (settings != null)
        {
            SelectValue(themeComboBox, EmptyTo(settings.Theme, "keep"));
            SelectValue(skinComboBox, EmptyTo(settings.Skin, "native"));
            SelectValue(petComboBox, EmptyTo(settings.Pet, "keep"));
        }

        SetCredentialHint(controller.HasCredential);
        SetStatus("已加载本机界面设置。");
    }

    private void SetOperationsEnabled(bool enabled)
    {
        applyButton.Enabled = enabled;
        openCodexButton.Enabled = enabled;
        restoreButton.Enabled = enabled;
        rechargeButton.Enabled = enabled;
        galleryButton.Enabled = true;
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
        if (hasCredential)
        {
            credentialHintLabel.Text = "留空可保留已有密钥。";
        }
        else
        {
            credentialHintLabel.Text = "粘贴 API Key，仅在本机加密保存。";
        }
    }

    private async Task ApplyAsync()
    {
        UserSettings settings = new UserSettings();
        settings.BaseUrl = AppController.OfficialRelayUrl;
        settings.Model = AppController.RelayModel;
        settings.Theme = SelectedValueOrText(themeComboBox, "keep");
        settings.Skin = SelectedValueOrText(skinComboBox, "native");
        settings.Pet = SelectedValueOrText(petComboBox, "keep");

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
            SetStatus("已打开 Codex。新建任务使用中转；在 Codex 输入 /pet 可显示宠物。");
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

    private void OpenSkinGallery(object sender, EventArgs e)
    {
        try
        {
            using (ThemeGalleryForm form = new ThemeGalleryForm(themeGalleryService, previewMode))
            {
                form.ShowDialog(this);
                if (!String.IsNullOrWhiteSpace(form.AppliedThemeId))
                    SetStatus("已应用主题：" + form.AppliedThemeId);
            }
        }
        catch (Exception ex)
        {
            SetStatus("打开主题图库失败：" + ex.Message);
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

    private void OpenRecharge(object sender, EventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppController.OfficialRechargeUrl) { UseShellExecute = true });
            SetStatus("已在浏览器打开充值中心。");
        }
        catch (Exception ex)
        {
            SetStatus("打开充值中心失败：" + ex.Message);
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
        int height = items.Length == 0 ? 0 : 116;
        rootLayout.SuspendLayout();
        advertisementBanner.SetItems(items);
        rootLayout.RowStyles[1].Height = height;
        rootLayout.AutoScrollMinSize = new Size(0, height == 0 ? 0 : BaseClientHeight + height);
        AdjustAdvertisementWindowHeight();
        rootLayout.ResumeLayout(true);
    }

    private void AdjustAdvertisementWindowHeight()
    {
        int height = (int)rootLayout.RowStyles[1].Height;
        if (WindowState != FormWindowState.Normal || height == advertisementWindowRowHeight) return;
        int availableHeight = Screen.FromControl(this).WorkingArea.Height - (Height - ClientSize.Height);
        int baseHeight = ClientSize.Height - advertisementWindowHeightAdjustment;
        int nextHeight = height == 0 ? Math.Max(BaseClientHeight, baseHeight)
            : Math.Max(ClientSize.Height, Math.Min(availableHeight, baseHeight + height));
        advertisementWindowRowHeight = height;
        advertisementWindowHeightAdjustment = height == 0 ? 0 : nextHeight - baseHeight;
        ClientSize = new Size(ClientSize.Width, nextHeight);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (rootLayout != null && rootLayout.RowStyles.Count > 1 && IsHandleCreated &&
            (int)rootLayout.RowStyles[1].Height != advertisementWindowRowHeight)
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
        applyButton.Enabled = !busy && !previewMode;
        openCodexButton.Enabled = !busy && !previewMode;
        restoreButton.Enabled = !busy && !previewMode;
        galleryButton.Enabled = !busy;
        officialWebsiteButton.Enabled = !busy;
        rechargeButton.Enabled = !busy && !previewMode;
        updateButton.Enabled = !busy && !previewMode && !checkingUpdate;
        if (!String.IsNullOrEmpty(message))
        {
            SetStatus(message);
        }
    }

    private void SetStatus(string text)
    {
        statusLabel.Text = text;
    }

    private void SelectValue(ComboBox comboBox, string value)
    {
        if (comboBox.DataSource == null)
        {
            comboBox.Text = value;
            return;
        }

        comboBox.SelectedValue = value;
        if (comboBox.SelectedValue == null || comboBox.SelectedValue.ToString() != value)
        {
            comboBox.Text = value;
        }
    }

    private string SelectedValueOrText(ComboBox comboBox, string fallback)
    {
        if (comboBox.SelectedValue != null)
        {
            return comboBox.SelectedValue.ToString();
        }
        if (!String.IsNullOrWhiteSpace(comboBox.Text))
        {
            return comboBox.Text.Trim();
        }
        return fallback;
    }

    private string EmptyTo(string value, string fallback)
    {
        return String.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private void UpdateThemePreview()
    {
        themePreviewPanel.Invalidate();
    }

    private void PaintThemePreview(object sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Choice choice = themeComboBox.SelectedItem as Choice;
        Color c1 = ParseColor(choice == null ? null : choice.Background, panelBack);
        Color c2 = ParseColor(choice == null ? null : choice.Accent, accent);
        Color c3 = ParseColor(choice == null ? null : choice.Foreground, textColor);
        var miniature = new RectangleF(12, 10, 72, 56);
        using (var path = DesktopPalette.Round(miniature, 6))
        using (var brush = new SolidBrush(c1))
        using (Pen pen = new Pen(borderColor))
        {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(pen, path);
        }
        using (var brush = new SolidBrush(c2))
        {
            e.Graphics.FillEllipse(brush, 19, 17, 5, 5);
            using (var path = DesktopPalette.Round(new RectangleF(36, 46, 38, 11), 3))
                e.Graphics.FillPath(brush, path);
        }
        using (var pen = new Pen(Color.FromArgb(125, c3), 2))
        {
            e.Graphics.DrawLine(pen, 20, 31, 27, 31);
            e.Graphics.DrawLine(pen, 20, 38, 27, 38);
            e.Graphics.DrawLine(pen, 36, 31, 74, 31);
            e.Graphics.DrawLine(pen, 36, 38, 62, 38);
        }
        var caption = new Rectangle(98, 3, Math.Max(0, themePreviewPanel.Width - 110), 24);
        DesktopPalette.DrawText(e.Graphics, "配色示意 · " + (choice == null ? "跟随当前主题" : choice.Label),
            Font, caption, mutedColor, StringAlignment.Near);
    }

    private Color ParseColor(string value, Color fallback)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        try
        {
            return ColorTranslator.FromHtml(value);
        }
        catch
        {
            return fallback;
        }
    }

    private class RoundedPanel : DesktopSurface
    {
        public Panel Body;
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
