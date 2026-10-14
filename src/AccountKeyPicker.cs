using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

// Revealed website keys stay in memory. Only successfully configured keys
// are persisted, using the existing per-account DPAPI cache.
internal sealed class AccountKeyPicker : DesktopSurface, IMessageFilter
{
    private readonly ListBox keys;
    private readonly Label message;
    private readonly string directory;
    private string userId;
    private AccountApiKey[] entries = new AccountApiKey[0];
    private bool expanded, updating;
    private bool readingSecret;
    private AccountApiKey loadingKey;
    private int selectionGeneration;
    private int hoveredIndex = -1;
    private string activeKey;
    internal Control AnchorControl;
    internal Func<AccountApiKey, Task<string>> RevealKey;
    internal event Action HeightChanged;
    internal event Action<string> KeyChosen;
    internal event Action<string> GuidanceChanged;
    internal bool HasAccount { get { return userId != null; } }
    internal bool Expanded { get { return expanded; } }
    internal int LogicalHeight { get { return (int)Math.Ceiling(RequiredHeight(Width) * 13F / Font.Size); } }

    internal AccountKeyPicker(string directory)
    {
        this.directory = directory;
        Font = new Font("Segoe UI", 13F, FontStyle.Regular, GraphicsUnit.Pixel);
        Margin = Padding.Empty; Dock = DockStyle.None; CornerRadius = 8;
        keys = new ListBox { DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false, BorderStyle = BorderStyle.None,
            BackColor = DesktopPalette.Surface, ForeColor = DesktopPalette.Text,
            AccessibleName = "选择已登录账户的 API Key", Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Pixel) };
        message = new Label { ForeColor = DesktopPalette.Muted, UseMnemonic = false, UseCompatibleTextRendering = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Pixel) };
        Controls.AddRange(new Control[] { keys, message });
        keys.DrawItem += DrawKey;
        keys.SelectedIndexChanged += delegate {
            if (!updating && readingSecret && loadingKey != null && keys.Items.Contains(loadingKey) && keys.SelectedItem != loadingKey)
                keys.SelectedItem = loadingKey;
        };
        keys.MouseMove += delegate(object sender, MouseEventArgs e) {
            int index = keys.IndexFromPoint(e.Location);
            if (index != hoveredIndex) { hoveredIndex = index; keys.Invalidate(); }
        };
        keys.MouseLeave += delegate { hoveredIndex = -1; keys.Invalidate(); };
        keys.MouseClick += delegate(object sender, MouseEventArgs e) {
            int index = keys.IndexFromPoint(e.Location);
            if (index >= 0) { keys.SelectedIndex = index; SelectKey(); }
        };
        keys.KeyDown += delegate(object sender, KeyEventArgs e) {
            if (e.KeyCode == Keys.Enter) { SelectKey(); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Escape) { Collapse(); e.SuppressKeyPress = true; }
        };
        Resize += delegate { RefreshLayout(); };
        Visible = false;
    }

    internal void Toggle()
    {
        if (expanded) Collapse(); else Open(false);
    }

    internal void Open(bool focusList)
    {
        if (!HasAccount || !Enabled) return;
        bool changed = !expanded;
        expanded = true;
        Visible = true;
        if (changed) Application.AddMessageFilter(this);
        RefreshLayout();
        if (changed && HeightChanged != null) HeightChanged();
        if (focusList && entries.Length > 0) keys.Focus();
    }

    internal void Collapse()
    {
        if (!expanded) return;
        expanded = false;
        selectionGeneration++;
        Application.RemoveMessageFilter(this);
        Visible = false;
        if (HeightChanged != null) HeightChanged();
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (expanded && (m.Msg == 0x201 || m.Msg == 0x204 || m.Msg == 0x207 || m.Msg == 0xA1))
        {
            Point point = Control.MousePosition;
            if (!RectangleToScreen(ClientRectangle).Contains(point) &&
                (AnchorControl == null || !AnchorControl.RectangleToScreen(AnchorControl.ClientRectangle).Contains(point))) Collapse();
        }
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { selectionGeneration++; Application.RemoveMessageFilter(this); }
        base.Dispose(disposing);
    }

    internal void SetActiveKey(string value) { activeKey = value; keys.Invalidate(); }

    private int Pixels(int value) { return (int)Math.Round(value * Font.Size / 13F); }
    private int MessageHeight(int width)
    {
        using (var graphics = message.CreateGraphics())
            return Math.Max(Pixels(16), (int)Math.Ceiling(graphics.MeasureString(String.IsNullOrEmpty(message.Text) ? " " : message.Text,
                message.Font, Math.Max(1, width)).Height));
    }

    internal int RequiredHeight(int width)
    {
        return !HasAccount || !expanded ? 0 : Pixels(12) + (entries.Length == 0 ? MessageHeight(width - Pixels(24)) : Math.Min(entries.Length, 4) * Pixels(44));
    }

    internal int FitHeight(int width, int available)
    {
        int height = RequiredHeight(width);
        if (entries.Length == 0) return Math.Min(height, available);
        int rows = Math.Max(1, Math.Min(4, (available - Pixels(12)) / Pixels(44)));
        return Math.Min(height, Pixels(12) + rows * Pixels(44));
    }

    private void RefreshLayout()
    {
        if (keys == null) return;
        keys.Visible = expanded && HasAccount && entries.Length > 0;
        message.Visible = expanded && HasAccount && entries.Length == 0;
        keys.ItemHeight = Pixels(44);
        keys.SetBounds(Pixels(6), Pixels(6), Math.Max(1, ClientSize.Width - Pixels(12)), Math.Max(1, ClientSize.Height - Pixels(12)));
        message.SetBounds(Pixels(12), Pixels(6), Math.Max(1, ClientSize.Width - Pixels(24)), MessageHeight(ClientSize.Width - Pixels(24)));
    }

    private void DrawKey(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= entries.Length) return;
        var entry = (AccountApiKey)keys.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        e.Graphics.FillRectangle(SystemBrushes.Window, e.Bounds);
        using (var fill = new SolidBrush(selected ? DesktopPalette.Selected : e.Index == hoveredIndex ? DesktopPalette.Page : keys.BackColor))
        using (var shape = DesktopPalette.Round(new RectangleF(e.Bounds.Left, e.Bounds.Top + 1, e.Bounds.Width, e.Bounds.Height - 2), Pixels(5)))
            e.Graphics.FillPath(fill, shape);
        var bounds = new Rectangle(e.Bounds.Left + Pixels(10), e.Bounds.Top + Pixels(3), Math.Max(0, e.Bounds.Width - Pixels(40)), Pixels(20));
        DesktopPalette.DrawText(e.Graphics, entry.Name, keys.Font, bounds,
            entry.Usable ? DesktopPalette.Text : DesktopPalette.Muted, StringAlignment.Near);
        bounds.Y += Pixels(19); bounds.Height = Pixels(17);
        string state = entry.Usable ? "可用" : entry.Status == "REVOKED" ? "已撤销" : entry.Status == "DISABLED" ? "已停用" : "已过期 / 不可用";
        if (readingSecret && entry == loadingKey) state = "正在读取…";
        bool active = Matches(entry, activeKey);
        DesktopPalette.DrawText(e.Graphics, entry.Prefix + "… · " + (active ? "正在使用" : state), message.Font, bounds, DesktopPalette.Muted, StringAlignment.Near);
        if (active)
        {
            int x = e.Bounds.Right - Pixels(20), y = e.Bounds.Top + e.Bounds.Height / 2;
            using (var pen = new Pen(DesktopPalette.Accent, Pixels(2)))
                e.Graphics.DrawLines(pen, new[] { new Point(x - Pixels(4), y), new Point(x, y + Pixels(4)), new Point(x + Pixels(7), y - Pixels(4)) });
        }
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }

    private void SetMessage(string text)
    {
        message.Text = text;
        RefreshLayout();
        if (GuidanceChanged != null) GuidanceChanged(text);
        if (HeightChanged != null) HeightChanged();
    }

    internal void SetAccount(string account, AccountApiKey[] values, string error)
    {
        bool changedUser = userId != account;
        selectionGeneration++;
        string previousId = keys.SelectedItem is AccountApiKey ? ((AccountApiKey)keys.SelectedItem).Id : null;
        userId = account; entries = values ?? new AccountApiKey[0];
        updating = true;
        keys.Items.Clear();
        keys.Items.AddRange(entries.Cast<object>().ToArray());
        int index = Array.FindIndex(entries, item => !changedUser && item.Id == previousId);
        if (index < 0) index = Array.FindIndex(entries, item => item.Usable);
        keys.SelectedIndex = index >= 0 ? index : entries.Length > 0 ? 0 : -1;
        updating = false;
        if (changedUser || !HasAccount) Collapse();
        Visible = HasAccount && expanded;
        keys.Enabled = entries.Length > 0;
        SetMessage(error ?? (entries.Length == 0 ? "此账户暂无 API Key，可直接手动粘贴。" : "点击输入框选择账户 Key，也可清空后手动填写。"));
    }

    private string KeyPath(AccountApiKey entry)
    {
        string identity = userId + "\n" + entry.Id + "\n" + entry.Prefix;
        return Path.Combine(directory, "api-key-cache", PrivateFiles.Digest(PrivateFiles.Utf8.GetBytes(identity)).Replace('/', '_').Replace('+', '-') + ".bin");
    }

    private string CachedKey(AccountApiKey entry)
    {
        if (!entry.Usable || String.IsNullOrEmpty(userId)) return null;
        if (Matches(entry, entry.Secret)) return entry.Secret;
        string path = KeyPath(entry);
        if (!File.Exists(path) || new FileInfo(path).Length > 32768) return null;
        byte[] plain = null;
        try
        {
            plain = PrivateFiles.Unprotect(File.ReadAllBytes(path));
            string value = PrivateFiles.Utf8.GetString(plain);
            return Matches(entry, value) ? value : null;
        }
        catch { return null; }
        finally { if (plain != null) Array.Clear(plain, 0, plain.Length); }
    }

    private bool Matches(AccountApiKey entry, string secret)
    {
        return entry.Usable && !String.IsNullOrWhiteSpace(secret) && secret.Length > entry.Prefix.Length && secret.Length <= 8192
            && secret.All(c => c >= 33 && c <= 126) && secret.StartsWith(entry.Prefix, StringComparison.Ordinal)
            && entries.Count(item => secret.StartsWith(item.Prefix, StringComparison.Ordinal)) == 1;
    }

    private async void SelectKey()
    {
        if (updating || readingSecret || keys.SelectedItem == null) return;
        var entry = (AccountApiKey)keys.SelectedItem;
        string value = CachedKey(entry);
        int requestGeneration = ++selectionGeneration;
        if (entry.Usable && value == null && RevealKey != null)
        {
            readingSecret = true;
            loadingKey = entry;
            keys.UseWaitCursor = true;
            keys.Invalidate();
            try { value = await RevealKey(entry); }
            catch (Exception) { value = null; }
            finally { readingSecret = false; loadingKey = null; if (!IsDisposed) { keys.UseWaitCursor = false; keys.Invalidate(); } }
            if (IsDisposed || requestGeneration != selectionGeneration) return;
            if (!Matches(entry, value)) value = null;
        }
        SetMessage(!entry.Usable ? "此 Key 已停用、撤销或过期，请选择其他 Key。" : value == null
            ? "未能读取完整密钥，请重试或手动粘贴完整密钥。" : "已填入所选 Key；清空后可手动填写。");
        if (value != null && KeyChosen != null) KeyChosen(value);
        Collapse();
    }

    internal string AvailableKey(string existing)
    {
        if (!HasAccount) return null;
        // Prefer the configured credential even if an earlier list item also
        // has a cache, so restoring login does not switch an existing setup.
        var configured = entries.FirstOrDefault(item => Matches(item, existing));
        if (configured != null) { keys.SelectedItem = configured; return existing; }
        foreach (var entry in entries)
        {
            string cached = CachedKey(entry);
            if (cached != null) { keys.SelectedItem = entry; return cached; }
        }
        return null;
    }

    internal bool ContainsUsableKey(string secret)
    {
        return HasAccount && entries.Any(item => Matches(item, secret));
    }

    internal void Remember(string secret)
    {
        if (!HasAccount) return;
        var entry = entries.FirstOrDefault(item => Matches(item, secret));
        if (entry == null) return;
        byte[] plain = PrivateFiles.Utf8.GetBytes(secret);
        try { PrivateFiles.WriteAtomic(KeyPath(entry), PrivateFiles.Protect(plain)); SetMessage("已加密记住此 Key，下次可从列表选择。"); }
        catch { SetMessage("配置成功，但账户 Key 副本保存失败；下次可手动粘贴。"); }
        finally { Array.Clear(plain, 0, plain.Length); }
    }
}
