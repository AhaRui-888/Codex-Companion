using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

public sealed class ThemeGalleryItem
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Author { get; set; }
    public string Summary { get; set; }
    public string DetailUrl { get; set; }
    public string PreviewImagePath { get; set; }
    public string PreviewUrl { get; set; }
    public byte[] PreviewImageBytes { get; set; }
    public string PreviewState { get; set; }
    public string PreviewError { get; set; }
    public string Accent { get; set; }
    public string Background { get; set; }
    public string Foreground { get; set; }
    public bool Installable { get; set; }
    public string Availability { get; set; }
    public string AvailabilityDetail { get; set; }
    public override string ToString() { return String.IsNullOrWhiteSpace(Name) ? Id : Name; }
}

public sealed class ThemeApplyResult
{
    public bool Success { get; set; }
    public string ThemeId { get; set; }
    public string Message { get; set; }
}

public sealed class ThemePreviewResult
{
    public bool Success { get; set; }
    public byte[] Bytes { get; set; }
    public string State { get; set; }
    public string Error { get; set; }
}

public interface IThemeGalleryService
{
    Task<IList<ThemeGalleryItem>> LoadThemesAsync(CancellationToken cancellationToken);
    Task<ThemePreviewResult> LoadPreviewAsync(string themeId, CancellationToken cancellationToken);
    string GetCurrentThemeId();
    Task<ThemeApplyResult> ApplyAsync(string themeId);
    Task<ThemeApplyResult> LaunchAsync(string themeId);
    Task<ThemeApplyResult> RestartAsync(string themeId, CancellationToken cancellationToken);
    Task<ThemeApplyResult> RestoreAsync();
}

public sealed class ThemeGalleryForm : Form
{
    private const int PageSize = 12;
    private readonly IThemeGalleryService service;
    private readonly bool previewOnly;
    private readonly CancellationTokenSource lifetimeCancellation = new CancellationTokenSource();
    private readonly TaskCompletionSource<bool> contentReadySource = new TaskCompletionSource<bool>();
    private readonly SemaphoreSlim previewGate = new SemaphoreSlim(4, 4);
    private readonly List<ThemeGalleryItem> allItems = new List<ThemeGalleryItem>();
    private readonly HashSet<string> previewLoading = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> previewLoaded = new HashSet<string>(StringComparer.Ordinal);
    private bool previewRefreshPending;
    private bool resizingCards;
    private readonly Color pageBack = DesktopPalette.Page;
    private readonly Color panelBack = DesktopPalette.Surface;
    private readonly Color textColor = DesktopPalette.Text;
    private readonly Color mutedColor = DesktopPalette.Muted;
    private readonly Color borderColor = DesktopPalette.Border;
    private readonly Color accent = DesktopPalette.Accent;

    private TextBox searchBox;
    private ComboBox availabilityComboBox;
    private FlowLayoutPanel cardsPanel;
    private Label selectionLabel;
    private System.Windows.Forms.Timer filterRefreshTimer;
    private Label statusLabel;
    private Button restoreButton;
    private Button closeButton;
    private Button previousPageButton;
    private Button nextPageButton;
    private Label pageLabel;
    private ThemeGalleryItem firstPreviewItem;
    private ThemeCard selectedCard;
    private int currentPage = 1;
    private int pageCount = 1;
    private int filteredCount;
    private bool disposed;
    private bool actionBusy;
    private CancellationTokenSource restartCancellation;

    public string AppliedThemeId { get; private set; }
    public Task ContentReady { get { return contentReadySource.Task; } }

    public ThemeGalleryForm(IThemeGalleryService service, bool previewOnly)
    {
        this.service = service;
        this.previewOnly = previewOnly;
        InitializeForm();
        BuildLayout();
        FormClosing += ThemeGalleryFormClosing;
        LoadThemes();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            lifetimeCancellation.Cancel();
            if (restartCancellation != null)
            {
                restartCancellation.Cancel();
                restartCancellation.Dispose();
                restartCancellation = null;
            }
            if (filterRefreshTimer != null)
            {
                filterRefreshTimer.Stop();
                filterRefreshTimer.Dispose();
                filterRefreshTimer = null;
            }
            previewGate.Dispose();
            lifetimeCancellation.Dispose();
        }
        base.Dispose(disposing);
    }

    public void SavePreview(string path)
    {
        using (Bitmap bitmap = new Bitmap(Width, Height))
        {
            DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(path);
        }
    }

    private void InitializeForm()
    {
        Text = previewOnly ? "Codex 主题预览" : "Codex 主题图库";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 620);
        ClientSize = new Size(1040, 700);
        BackColor = pageBack;
        ForeColor = textColor;
        Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Regular, GraphicsUnit.Pixel);
        AutoScaleMode = AutoScaleMode.None;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DesktopPalette.LightTitleBar(this);
    }

    private void BuildLayout()
    {
        TableLayoutPanel root = new TableLayoutPanel();
        root.Dock = DockStyle.Fill;
        root.Padding = new Padding(16);
        root.BackColor = pageBack;
        root.ColumnCount = 1;
        root.RowCount = 3;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, Math.Max(88, Font.Height * 3 + 22)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, Math.Max(46, Font.Height + 24)));
        Controls.Add(root);
        root.Controls.Add(BuildFilters(), 0, 0);
        cardsPanel = new FlowLayoutPanel();
        cardsPanel.Dock = DockStyle.Fill;
        cardsPanel.AutoScroll = true;
        cardsPanel.WrapContents = true;
        cardsPanel.FlowDirection = FlowDirection.LeftToRight;
        cardsPanel.Padding = new Padding(8);
        cardsPanel.BackColor = pageBack;
        cardsPanel.Resize += delegate { ResizeCards(); EnsureVisiblePreviews(); };
        cardsPanel.Scroll += delegate { ScheduleVisiblePreviews(); };
        cardsPanel.MouseWheel += delegate { ScheduleVisiblePreviews(); };
        root.Controls.Add(cardsPanel, 0, 1);
        root.Controls.Add(BuildActions(), 0, 2);
    }

    private Control BuildFilters()
    {
        Panel filters = new Panel();
        filters.Dock = DockStyle.Fill;
        Panel controls = new Panel();
        controls.Dock = DockStyle.Top;
        controls.Height = Math.Max(38, Font.Height + 16);
        Panel searchPanel = new Panel();
        searchPanel.Dock = DockStyle.Fill;
        searchPanel.Padding = new Padding(0, 0, 12, 0);
        Label searchLabel = new Label();
        searchLabel.Dock = DockStyle.Left;
        searchLabel.Width = 76;
        searchLabel.Text = "搜索主题";
        searchLabel.ForeColor = mutedColor;
        searchLabel.TextAlign = ContentAlignment.MiddleLeft;
        searchBox = new TextBox { BackColor = DesktopPalette.Input, ForeColor = DesktopPalette.Text, BorderStyle = BorderStyle.None, AccessibleName = "搜索主题" };
        searchBox.Dock = DockStyle.Fill;
        searchBox.TextChanged += delegate { ScheduleCardRefresh(); };
        var searchFrame = new DesktopSurface { Dock = DockStyle.Fill, CornerRadius = 10, Padding = new Padding(12, 9, 12, 6) };
        searchFrame.Controls.Add(searchBox);
        searchBox.Enter += delegate { searchFrame.BorderColor = accent; searchFrame.Invalidate(); };
        searchBox.Leave += delegate { searchFrame.BorderColor = borderColor; searchFrame.Invalidate(); };
        searchPanel.Controls.Add(searchFrame);
        searchPanel.Controls.Add(searchLabel);
        availabilityComboBox = new DesktopComboBox { AccessibleName = "主题可用性筛选" };
        availabilityComboBox.Dock = DockStyle.Right;
        availabilityComboBox.Width = 150;
        availabilityComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        availabilityComboBox.Items.Add("全部主题");
        availabilityComboBox.Items.Add("可直接应用");
        availabilityComboBox.Items.Add("网站参考");
        availabilityComboBox.SelectedIndex = 0;
        availabilityComboBox.SelectedIndexChanged += delegate { ScheduleCardRefresh(); };
        controls.Controls.Add(searchPanel);
        controls.Controls.Add(availabilityComboBox);
        filterRefreshTimer = new System.Windows.Forms.Timer { Interval = 180 };
        filterRefreshTimer.Tick += delegate
        {
            filterRefreshTimer.Stop();
            RefreshCards();
        };
        selectionLabel = new Label();
        selectionLabel.Dock = DockStyle.Bottom;
        selectionLabel.Height = Math.Max(44, selectionLabel.Font.Height * 2 + 8);
        selectionLabel.AutoEllipsis = false;
        selectionLabel.ForeColor = mutedColor;
        selectionLabel.TextAlign = ContentAlignment.MiddleLeft;
        selectionLabel.Text = "单击选择，双击预览。首次使用请点“应用并重启”；启用外观后可点“应用”。";
        filters.Controls.Add(selectionLabel);
        filters.Controls.Add(controls);
        return filters;
    }

    private Control BuildActions()
    {
        var actions = new TableLayoutPanel { ColumnCount = 6, RowCount = 1, Margin = Padding.Empty };
        actions.Dock = DockStyle.Fill;
        actions.Padding = new Padding(0, 8, 0, 0);
        foreach (int width in new[] { 136, 90, 90 })
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        closeButton = CreateButton("关闭", 76, false);
        closeButton.DialogResult = DialogResult.Cancel;
        restoreButton = CreateButton("恢复原生主题", 164, false);
        restoreButton.Click += RestoreTheme;
        nextPageButton = CreateButton("下一页", 106, false);
        nextPageButton.Click += NextPage;
        previousPageButton = CreateButton("上一页", 106, false);
        previousPageButton.Click += PreviousPage;
        pageLabel = new Label { Width = 136, Height = Math.Max(32, Font.Height + 14), TextAlign = ContentAlignment.MiddleCenter, ForeColor = mutedColor, AutoEllipsis = false };
        statusLabel = new Label();
        statusLabel.Width = 270;
        statusLabel.Height = Math.Max(32, statusLabel.Font.Height + 14);
        statusLabel.AutoEllipsis = true;
        statusLabel.ForeColor = mutedColor;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        Control[] controls = { pageLabel, previousPageButton, nextPageButton, statusLabel, restoreButton, closeButton };
        for (int i = 0; i < controls.Length; i++)
        {
            controls[i].Dock = DockStyle.Fill;
            controls[i].Margin = new Padding(4, 0, 4, 0);
            actions.Controls.Add(controls[i], i, 0);
        }
        AcceptButton = closeButton;
        CancelButton = closeButton;
        return actions;
    }

    private Button CreateButton(string text, int width, bool primary)
    {
        Button button = new DesktopButton
        {
            Text = text,
            Width = width,
            Font = new Font(Font.FontFamily, 13F, FontStyle.Regular, GraphicsUnit.Pixel)
        };
        button.Height = Math.Max(28, button.Font.Height + 10);
        if (primary)
        {
            button.BackColor = accent;
            button.ForeColor = Color.White;
            ((DesktopButton)button).Primary = true;
            button.UseVisualStyleBackColor = false;
        }
        return button;
    }

    private async void LoadThemes()
    {
        allItems.Clear();
        try
        {
            if (service == null)
            {
                SetStatus("主题服务未连接。");
                contentReadySource.TrySetException(new InvalidOperationException("主题服务未连接。"));
                return;
            }
            SetStatus("正在读取全部主题...");
            IList<ThemeGalleryItem> items = await service.LoadThemesAsync(lifetimeCancellation.Token);
            if (disposed) return;
            if (items != null) allItems.AddRange(items);
            if (allItems.Count == 0)
            {
                SetStatus("暂无可显示的主题。");
                contentReadySource.TrySetException(new InvalidDataException("主题目录为空。"));
                return;
            }
            firstPreviewItem = allItems[0];
            RefreshCards();
            SetStatus(CatalogStatus());
        }
        catch (OperationCanceledException) { contentReadySource.TrySetCanceled(); }
        catch (Exception ex)
        {
            SetStatus("读取主题图库失败：" + ex.Message);
            contentReadySource.TrySetException(ex);
        }
    }

    private void RefreshCards()
    {
        if (cardsPanel == null || disposed) return;
        selectedCard = null;
        if (selectionLabel != null) selectionLabel.Text = "单击选择，双击预览。首次使用请点“应用并重启”；启用外观后可点“应用”。";
        List<ThemeGalleryItem> filteredItems = new List<ThemeGalleryItem>();
        string search = searchBox == null ? "" : searchBox.Text.Trim();
        int mode = availabilityComboBox == null ? 0 : availabilityComboBox.SelectedIndex;
        foreach (ThemeGalleryItem item in allItems)
        {
            if (mode == 1 && !item.Installable) continue;
            if (mode == 2 && item.Installable) continue;
            string haystack = (item.Name ?? "") + " " + (item.Author ?? "") + " " + (item.Summary ?? "");
            if (search.Length > 0 && haystack.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
            filteredItems.Add(item);
        }
        filteredCount = filteredItems.Count;
        pageCount = Math.Max(1, (filteredCount + PageSize - 1) / PageSize);
        if (currentPage > pageCount) currentPage = pageCount;
        int first = Math.Min(filteredCount, (currentPage - 1) * PageSize);
        int last = Math.Min(filteredCount, first + PageSize);
        cardsPanel.SuspendLayout();
        try
        {
            while (cardsPanel.Controls.Count > 0) cardsPanel.Controls[0].Dispose();
            for (int index = first; index < last; index++) cardsPanel.Controls.Add(CreateCard(filteredItems[index]));
        }
        finally { cardsPanel.ResumeLayout(true); }
        ResizeCards();
        EnsureVisiblePreviews();
        UpdatePageControls();
        if (cardsPanel.Controls.Count == 0) SetStatus("没有匹配的主题。");
    }

    private void ScheduleCardRefresh()
    {
        currentPage = 1;
        if (filterRefreshTimer == null || disposed)
        {
            RefreshCards();
            return;
        }
        filterRefreshTimer.Stop();
        filterRefreshTimer.Start();
    }

    private void PreviousPage(object sender, EventArgs e)
    {
        if (actionBusy || currentPage <= 1) return;
        currentPage--;
        RefreshCards();
    }

    private void NextPage(object sender, EventArgs e)
    {
        if (actionBusy || currentPage >= pageCount) return;
        currentPage++;
        RefreshCards();
    }

    private void UpdatePageControls()
    {
        if (pageLabel == null) return;
        pageLabel.Text = "第 " + currentPage + "/" + pageCount + " 页（" + filteredCount + "项）";
        previousPageButton.Enabled = !actionBusy && currentPage > 1;
        nextPageButton.Enabled = !actionBusy && currentPage < pageCount;
    }

    private string CatalogStatus()
    {
        int installable = 0;
        foreach (ThemeGalleryItem item in allItems)
            if (item != null && item.Installable) installable++;
        return "可应用 " + installable + " / 共 " + allItems.Count;
    }

    private ThemeCard CreateCard(ThemeGalleryItem item)
    {
        ThemeCard card = new ThemeCard(item, panelBack, textColor, mutedColor, borderColor);
        card.ApplyClicked += ApplyCardTheme;
        card.LaunchClicked += LaunchCardTheme;
        card.RestartClicked += RestartCardTheme;
        card.DetailsClicked += OpenDetails;
        card.SelectClicked += SelectCard;
        card.PreviewClicked += OpenImagePreview;
        UpdateCardState(card);
        return card;
    }

    private void SelectCard(ThemeCard card)
    {
        if (card == null || disposed) return;
        if (selectedCard != null && !selectedCard.IsDisposed) selectedCard.SetSelected(false);
        selectedCard = card;
        selectedCard.SetSelected(true);
        if (selectionLabel != null)
            selectionLabel.Text = "已选择：" + (card.Item.Name ?? card.Item.Id) +
                (card.Item.Installable ? "。首次使用请点“应用并重启”；启用外观后可点“应用”。" : "。该主题仅预览或暂不可用。");
    }

    private void ResizeCards()
    {
        if (cardsPanel == null || resizingCards) return;
        resizingCards = true;
        try
        {
            int available = Math.Max(300, cardsPanel.DisplayRectangle.Width - cardsPanel.Padding.Horizontal);
            int columns = 3;
            int cardMargin = cardsPanel.Controls.Count > 0 ? ((ThemeCard)cardsPanel.Controls[0]).Margin.Horizontal : 10;
            int width = Math.Max(238, (available - cardMargin * columns) / columns);
            foreach (ThemeCard card in cardsPanel.Controls)
            {
                card.Width = width;
                card.LayoutCard();
            }
            cardsPanel.PerformLayout();
        }
        finally { resizingCards = false; }
        UpdateScrollExtent();
    }

    private void UpdateScrollExtent()
    {
        if (cardsPanel == null || resizingCards) return;
        int contentBottom = cardsPanel.Padding.Top;
        int scrollOffset = -cardsPanel.AutoScrollPosition.Y;
        foreach (ThemeCard card in cardsPanel.Controls)
            contentBottom = Math.Max(contentBottom, card.Bottom + scrollOffset + card.Margin.Bottom);
        int height = contentBottom + cardsPanel.Padding.Bottom + 8;
        if (cardsPanel.AutoScrollMinSize.Height == height) return;
        resizingCards = true;
        try { cardsPanel.AutoScrollMinSize = new Size(0, height); }
        finally { resizingCards = false; }
    }

    private void EnsureVisiblePreviews()
    {
        if (cardsPanel == null || service == null || disposed) return;
        Rectangle viewport = cardsPanel.ClientRectangle;
        foreach (ThemeCard card in cardsPanel.Controls)
            if (card.Bounds.IntersectsWith(viewport)) StartPreview(card);
    }

    private void ScheduleVisiblePreviews()
    {
        if (cardsPanel == null || disposed || previewRefreshPending) return;
        previewRefreshPending = true;
        try
        {
            if (cardsPanel.IsHandleCreated)
            {
                cardsPanel.BeginInvoke((MethodInvoker)delegate
                {
                    previewRefreshPending = false;
                    EnsureVisiblePreviews();
                });
            }
            else
            {
                previewRefreshPending = false;
                EnsureVisiblePreviews();
            }
        }
        catch (InvalidOperationException)
        {
            previewRefreshPending = false;
            if (!disposed) EnsureVisiblePreviews();
        }
    }

    private async void StartPreview(ThemeCard card)
    {
        ThemeGalleryItem item = card.Item;
        if (item == null || previewLoaded.Contains(item.Id) || previewLoading.Contains(item.Id)) return;
        if (item.PreviewImageBytes != null && item.PreviewImageBytes.Length > 0)
        {
            card.SetPreview(item.PreviewImageBytes);
            previewLoaded.Add(item.Id);
            if (item == firstPreviewItem) contentReadySource.TrySetResult(true);
            return;
        }
        previewLoading.Add(item.Id);
        card.SetPreviewStatus("加载预览…");
        try
        {
            await previewGate.WaitAsync(lifetimeCancellation.Token);
            try
            {
                ThemePreviewResult result = await service.LoadPreviewAsync(item.Id, lifetimeCancellation.Token);
                if (disposed || card.IsDisposed) return;
                if (result != null && result.Success && result.Bytes != null)
                {
                    item.PreviewImageBytes = result.Bytes;
                    item.PreviewState = result.State;
                    item.PreviewError = result.Error;
                    card.SetPreview(result.Bytes);
                    previewLoaded.Add(item.Id);
                    if (item == firstPreviewItem) contentReadySource.TrySetResult(true);
                }
                else
                {
                    string error = result == null ? "预览返回为空。" : result.Error;
                    card.SetPreviewStatus(String.IsNullOrWhiteSpace(error) ? "暂无预览图" : error);
                    if (item == firstPreviewItem) contentReadySource.TrySetException(new InvalidOperationException(error ?? "首项预览不可用。"));
                }
            }
            finally { previewGate.Release(); }
        }
        catch (OperationCanceledException) { if (item == firstPreviewItem) contentReadySource.TrySetCanceled(); }
        catch (Exception ex)
        {
            if (!disposed && !card.IsDisposed) card.SetPreviewStatus("预览失败：" + ex.Message);
            if (item == firstPreviewItem) contentReadySource.TrySetException(ex);
        }
        finally
        {
            previewLoading.Remove(item.Id);
        }
    }

    private void UpdateCardState(ThemeCard card)
    {
        if (card != null) card.SetActionsEnabled(!actionBusy && !previewOnly && service != null && card.Item.Installable);
    }

    private async void ApplyCardTheme(ThemeCard card)
    {
        if (card == null || actionBusy || previewOnly || service == null || !card.Item.Installable) return;
        SelectCard(card);
        SetActionBusy(true, "正在应用主题到 Codex...");
        try
        {
            ThemeApplyResult result = await service.ApplyAsync(card.Item.Id);
            if (disposed) return;
            if (result == null || !result.Success)
            {
                ShowThemeFailure("主题应用失败", result == null ? "主题服务未返回结果。" : result.Message);
                return;
            }
            AppliedThemeId = result.ThemeId ?? card.Item.Id;
            SetStatus(String.IsNullOrWhiteSpace(result.Message) ? "主题已应用到 Codex。" : result.Message);
        }
        catch (Exception ex) { ShowThemeFailure("主题应用失败", ex.Message); }
        finally { SetActionBusy(false, null); }
    }

    private async void LaunchCardTheme(ThemeCard card)
    {
        if (card == null || actionBusy || previewOnly || service == null || !card.Item.Installable) return;
        SelectCard(card);
        SetActionBusy(true, "正在启用外观并启动 Codex...");
        try
        {
            ThemeApplyResult result = await service.LaunchAsync(card.Item.Id);
            if (disposed) return;
            if (result == null || !result.Success)
            {
                ShowThemeFailure("启用外观失败", result == null ? "主题服务未返回结果。" : result.Message);
                return;
            }
            AppliedThemeId = result.ThemeId ?? card.Item.Id;
            SetStatus(String.IsNullOrWhiteSpace(result.Message) ? "已启用外观并启动 Codex。" : result.Message);
        }
        catch (Exception ex) { ShowThemeFailure("启用外观失败", ex.Message); }
        finally { SetActionBusy(false, null); }
    }

    private async void RestartCardTheme(ThemeCard card)
    {
        if (card == null || actionBusy || previewOnly || service == null || !card.Item.Installable) return;
        SelectCard(card);
        if (MessageBox.Show(this, "应用主题并重启 Codex 会中断当前任务。主题包会先完成下载和校验，确认后才关闭 Codex。是否继续？",
            "确认重启 Codex", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        restartCancellation = new CancellationTokenSource();
        SetActionBusy(true, "正在验证主题并准备重启 Codex...");
        try
        {
            ThemeApplyResult result = await service.RestartAsync(card.Item.Id, restartCancellation.Token);
            if (disposed) return;
            if (result == null || !result.Success)
            {
                ShowThemeFailure("重启 Codex 失败", result == null ? "主题服务未返回结果。" : result.Message);
                return;
            }
            AppliedThemeId = result.ThemeId ?? card.Item.Id;
            SetStatus(String.IsNullOrWhiteSpace(result.Message) ? "主题已应用，Codex 已重启。" : result.Message);
        }
        catch (OperationCanceledException) { SetStatus("已取消重启，Codex 未被关闭。"); }
        catch (Exception ex) { ShowThemeFailure("重启 Codex 失败", ex.Message); }
        finally
        {
            if (restartCancellation != null) { restartCancellation.Dispose(); restartCancellation = null; }
            SetActionBusy(false, null);
        }
    }

    private async void RestoreTheme(object sender, EventArgs e)
    {
        if (actionBusy || previewOnly || service == null) return;
        SetActionBusy(true, "正在恢复原生主题...");
        try
        {
            ThemeApplyResult result = await service.RestoreAsync();
            if (disposed) return;
            if (result == null || !result.Success)
            {
                ShowThemeFailure("主题恢复失败", result == null ? "主题服务未返回结果。" : result.Message);
                return;
            }
            SetStatus(String.IsNullOrWhiteSpace(result.Message) ? "已恢复原生主题。" : result.Message);
        }
        catch (Exception ex) { ShowThemeFailure("主题恢复失败", ex.Message); }
        finally { SetActionBusy(false, null); }
    }

    private void OpenDetails(ThemeCard card)
    {
        if (card == null || String.IsNullOrWhiteSpace(card.Item.DetailUrl)) return;
        SelectCard(card);
        try { Process.Start(card.Item.DetailUrl); }
        catch (Exception ex) { SetStatus("打开主题详情失败：" + ex.Message); }
    }

    private void OpenImagePreview(ThemeCard card)
    {
        if (card == null) return;
        SelectCard(card);
        byte[] bytes = card.Item.PreviewImageBytes;
        if (bytes == null || bytes.Length == 0) return;
        using (Form form = new Form())
        using (PictureBox picture = new PictureBox())
        {
            form.Text = card.Item.Name ?? "主题预览";
            form.StartPosition = FormStartPosition.CenterParent;
            form.BackColor = DesktopPalette.Page;
            form.HandleCreated += delegate { DesktopPalette.LightTitleBar(form); };
            form.ClientSize = new Size(1000, 700);
            picture.Dock = DockStyle.Fill;
            picture.SizeMode = PictureBoxSizeMode.Zoom;
            using (MemoryStream stream = new MemoryStream(bytes))
            using (Image source = Image.FromStream(stream)) picture.Image = new Bitmap(source);
            form.Controls.Add(picture);
            form.ShowDialog(this);
        }
    }

    private void SetActionBusy(bool busy, string message)
    {
        if (disposed) return;
        actionBusy = busy;
        closeButton.Enabled = !busy;
        restoreButton.Enabled = !busy && !previewOnly && service != null;
        foreach (ThemeCard card in cardsPanel.Controls) card.SetActionsEnabled(!busy && !previewOnly && service != null && card.Item.Installable);
        UpdatePageControls();
        if (!String.IsNullOrWhiteSpace(message)) SetStatus(message);
    }

    private void SetStatus(string text)
    {
        if (statusLabel != null && !disposed) statusLabel.Text = String.IsNullOrWhiteSpace(text) ? "" : text;
    }

    private void ShowThemeFailure(string title, string detail)
    {
        SetStatus(title + "。");
        if (disposed) return;
        MessageBox.Show(this, String.IsNullOrWhiteSpace(detail) ? title + "。" : detail,
            title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void ThemeGalleryFormClosing(object sender, FormClosingEventArgs e)
    {
        if (!actionBusy) return;
        e.Cancel = true;
        if (restartCancellation != null) restartCancellation.Cancel();
        SetStatus("主题操作进行中，请等待完成后再关闭窗口。");
    }

    private sealed class ThemeCard : DesktopSurface
    {
        public readonly ThemeGalleryItem Item;
        private readonly PictureBox picture;
        private readonly Label title;
        private readonly Label meta;
        private readonly Label summary;
        private readonly Label status;
        private readonly Button apply;
        private readonly Button launch;
        private readonly Button restart;
        private readonly Button details;
        private readonly Color normalBack;
        private readonly Color normalText;
        private bool selected;
        private string lastStatus;
        private Image currentImage;
        public event Action<ThemeCard> ApplyClicked;
        public event Action<ThemeCard> LaunchClicked;
        public event Action<ThemeCard> RestartClicked;
        public event Action<ThemeCard> DetailsClicked;
        public event Action<ThemeCard> SelectClicked;
        public event Action<ThemeCard> PreviewClicked;
        public Image PreviewImage { get { return currentImage; } }

        public ThemeCard(ThemeGalleryItem item, Color panelBack, Color textColor, Color mutedColor, Color borderColor)
        {
            Item = item;
            Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Regular, GraphicsUnit.Pixel);
            normalBack = panelBack;
            normalText = textColor;
            BackColor = panelBack;
            BorderStyle = BorderStyle.None;
            Margin = new Padding(5);
            Height = 330;
            picture = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = DesktopPalette.Input, Cursor = Cursors.Hand };
            picture.Click += delegate { if (SelectClicked != null) SelectClicked(this); };
            picture.DoubleClick += delegate { if (PreviewClicked != null) PreviewClicked(this); };
            title = NewLabel(17F, FontStyle.Bold, textColor);
            title.Cursor = Cursors.Hand;
            title.Click += delegate { if (SelectClicked != null) SelectClicked(this); };
            meta = NewLabel(12F, FontStyle.Regular, mutedColor);
            summary = NewLabel(12F, FontStyle.Regular, textColor);
            summary.AutoEllipsis = true;
            status = NewLabel(12F, FontStyle.Regular, mutedColor);
            apply = NewButton("应用"); apply.Click += delegate { if (ApplyClicked != null) ApplyClicked(this); };
            launch = NewButton("启动"); launch.Click += delegate { if (LaunchClicked != null) LaunchClicked(this); };
            restart = NewButton("应用并重启"); restart.Click += delegate { if (RestartClicked != null) RestartClicked(this); };
            details = NewButton("详情"); details.Click += delegate { if (DetailsClicked != null) DetailsClicked(this); };
            Controls.Add(picture); Controls.Add(title); Controls.Add(meta); Controls.Add(summary); Controls.Add(status);
            Controls.Add(apply); Controls.Add(launch); Controls.Add(restart); Controls.Add(details);
            UpdateText();
            LayoutCard();
        }

        private Label NewLabel(float size, FontStyle style, Color color)
        {
            return new Label { ForeColor = color, Font = new Font(Font.FontFamily, size, style, GraphicsUnit.Pixel), AutoEllipsis = true };
        }

        private Button NewButton(string text)
        {
            return new DesktopButton
            {
                Text = text,
                Height = 28,
                Width = 74,
                Margin = new Padding(2),
                Font = new Font(Font.FontFamily, 12F, FontStyle.Regular, GraphicsUnit.Pixel)
            };
        }

        public void LayoutCard()
        {
            int width = Math.Max(238, Width);
            picture.SetBounds(8, 8, width - 16, Math.Max(110, (width - 16) * 3 / 5));
            int titleHeight = Math.Max(24, title.Font.Height + 6);
            int bodyHeight = Math.Max(20, meta.Font.Height + 6);
            int detailHeight = Math.Max(22, summary.Font.Height + 7);
            int statusHeight = Math.Max(22, status.Font.Height + 7);
            title.SetBounds(8, picture.Bottom + 7, width - 16, titleHeight);
            meta.SetBounds(8, title.Bottom, width - 16, bodyHeight);
            summary.SetBounds(8, meta.Bottom, width - 16, detailHeight);
            status.SetBounds(8, summary.Bottom, width - 16, statusHeight);
            int buttonHeight = Math.Max(28, apply.Font.Height + 10);
            int requiredHeight = status.Bottom + buttonHeight * 2 + 14;
            if (Height < requiredHeight) Height = requiredHeight;
            int buttonWidth = Math.Max(90, (width - 24) / 2);
            int firstRow = Height - buttonHeight * 2 - 8;
            int secondRow = firstRow + buttonHeight + 2;
            apply.SetBounds(8, firstRow, buttonWidth, buttonHeight);
            launch.SetBounds(10 + buttonWidth, firstRow, buttonWidth, buttonHeight);
            restart.SetBounds(8, secondRow, buttonWidth, buttonHeight);
            details.SetBounds(10 + buttonWidth, secondRow, buttonWidth, buttonHeight);
        }

        public void SetActionsEnabled(bool enabled)
        {
            apply.Enabled = enabled;
            launch.Enabled = enabled;
            restart.Enabled = enabled;
            details.Enabled = !String.IsNullOrWhiteSpace(Item.DetailUrl);
        }

        public void SetSelected(bool value)
        {
            selected = value;
            BackColor = selected ? DesktopPalette.Selected : normalBack;
            BorderColor = selected ? DesktopPalette.Accent : DesktopPalette.Border;
            Invalidate();
            title.ForeColor = selected ? DesktopPalette.Accent : normalText;
            status.Text = selected ? "已选择 · " + (lastStatus ?? "") : (lastStatus ?? "");
        }

        private string AvailabilityText()
        {
            if (Item.Installable) return "可应用 · 应用时校验";
            return String.IsNullOrWhiteSpace(Item.AvailabilityDetail) ? "仅预览或暂不可用" :
                "仅预览或暂不可用：" + Item.AvailabilityDetail;
        }

        private void SetStatusText(string message)
        {
            lastStatus = String.IsNullOrWhiteSpace(message) ? "" : message;
            status.Text = selected ? "已选择 · " + lastStatus : lastStatus;
        }

        public void SetPreviewStatus(string message)
        {
            picture.Image = null;
            if (currentImage != null) { currentImage.Dispose(); currentImage = null; }
            SetColorPlaceholder();
            SetStatusText(message);
        }

        private void SetColorPlaceholder()
        {
            Bitmap placeholder = new Bitmap(Math.Max(1, picture.Width), Math.Max(1, picture.Height));
            Color background = ParseColor(Item.Background, picture.BackColor);
            Color accent = ParseColor(Item.Accent, DesktopPalette.Accent);
            using (Graphics graphics = Graphics.FromImage(placeholder))
            {
                graphics.Clear(background);
                using (Brush brush = new SolidBrush(accent))
                    graphics.FillRectangle(brush, 0, Math.Max(0, placeholder.Height - 22), placeholder.Width, 22);
            }
            currentImage = placeholder;
            picture.Image = currentImage;
        }

        private Color ParseColor(string value, Color fallback)
        {
            try { return String.IsNullOrWhiteSpace(value) ? fallback : ColorTranslator.FromHtml(value); }
            catch { return fallback; }
        }

        public void SetPreview(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) { SetPreviewStatus("暂无预览图"); return; }
            Bitmap thumbnail = null;
            try
            {
                using (MemoryStream stream = new MemoryStream(bytes))
                using (Image source = Image.FromStream(stream))
                {
                    int width = Math.Max(1, picture.Width);
                    int height = Math.Max(1, picture.Height);
                    thumbnail = new Bitmap(width, height);
                    using (Graphics graphics = Graphics.FromImage(thumbnail))
                    {
                        graphics.Clear(picture.BackColor);
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.SmoothingMode = SmoothingMode.HighQuality;
                        float scale = Math.Min((float)width / source.Width, (float)height / source.Height);
                        int drawWidth = Math.Max(1, (int)(source.Width * scale));
                        int drawHeight = Math.Max(1, (int)(source.Height * scale));
                        int x = (width - drawWidth) / 2;
                        int y = (height - drawHeight) / 2;
                        graphics.DrawImage(source, new Rectangle(x, y, drawWidth, drawHeight));
                    }
                }
                picture.Image = null;
                if (currentImage != null) currentImage.Dispose();
                currentImage = thumbnail;
                thumbnail = null;
                picture.Image = currentImage;
                SetStatusText(AvailabilityText());
            }
            catch { SetPreviewStatus("预览格式不可用"); }
            finally { if (thumbnail != null) thumbnail.Dispose(); }
        }

        private void UpdateText()
        {
            title.Text = Item.Name ?? Item.Id;
            meta.Text = String.IsNullOrWhiteSpace(Item.Author) ? "" : "作者：" + Item.Author;
            summary.Text = Item.Summary ?? "";
            SetStatusText(AvailabilityText());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                picture.Image = null;
                if (currentImage != null) { currentImage.Dispose(); currentImage = null; }
            }
            base.Dispose(disposing);
        }
    }
}
