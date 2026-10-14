using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

// Only the selected, successfully used full key is cached. Website metadata
// cannot recover plaintext: it must never be treated as a model credential.
internal sealed class AccountKeyPicker : Panel
{
    private readonly ListBox keys;
    private readonly Label message;
    private readonly string directory;
    private string userId;
    private AccountApiKey[] entries = new AccountApiKey[0];
    private bool expanded, updating;
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
        Margin = Padding.Empty; Dock = DockStyle.Fill;
        keys = new ListBox { DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false, BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(248, 250, 253), ForeColor = DesktopPalette.Text,
            AccessibleName = "选择已登录账户的 API Key", Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Pixel) };
        message = new Label { ForeColor = DesktopPalette.Muted, UseMnemonic = false, UseCompatibleTextRendering = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Pixel) };
        Controls.AddRange(new Control[] { keys, message });
        keys.DrawItem += DrawKey;
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
        RefreshLayout();
        if (changed && HeightChanged != null) HeightChanged();
        if (focusList && entries.Length > 0) keys.Focus();
    }

    internal void Collapse()
    {
        if (!expanded) return;
        expanded = false;
        Visible = false;
        if (HeightChanged != null) HeightChanged();
    }

    private int Pixels(int value) { return (int)Math.Round(value * Font.Size / 13F); }
    private int MessageHeight(int width)
    {
        using (var graphics = message.CreateGraphics())
            return Math.Max(Pixels(16), (int)Math.Ceiling(graphics.MeasureString(String.IsNullOrEmpty(message.Text) ? " " : message.Text,
                message.Font, Math.Max(1, width)).Height));
    }

    internal int RequiredHeight(int width)
    {
        return !HasAccount || !expanded ? 0 : Pixels(8) + (entries.Length == 0 ? MessageHeight(width) : Math.Min(entries.Length, 4) * Pixels(42));
    }

    private void RefreshLayout()
    {
        if (keys == null) return;
        keys.Visible = expanded && HasAccount && entries.Length > 0;
        message.Visible = expanded && HasAccount && entries.Length == 0;
        keys.ItemHeight = Pixels(42);
        keys.SetBounds(0, Pixels(6), ClientSize.Width, Math.Min(entries.Length, 4) * keys.ItemHeight);
        message.SetBounds(0, Pixels(6), ClientSize.Width, MessageHeight(ClientSize.Width));
    }

    private void DrawKey(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= entries.Length) return;
        var entry = (AccountApiKey)keys.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var fill = new SolidBrush(selected ? DesktopPalette.Selected : keys.BackColor))
            e.Graphics.FillRectangle(fill, e.Bounds);
        var bounds = new Rectangle(e.Bounds.Left + Pixels(10), e.Bounds.Top + Pixels(3), Math.Max(0, e.Bounds.Width - Pixels(20)), Pixels(19));
        DesktopPalette.DrawText(e.Graphics, entry.Name, keys.Font, bounds,
            entry.Usable ? DesktopPalette.Text : DesktopPalette.Muted, StringAlignment.Near);
        bounds.Y += Pixels(18); bounds.Height = Pixels(17);
        string state = entry.Usable ? "可用" : entry.Status == "REVOKED" ? "已撤销" : entry.Status == "DISABLED" ? "已停用" : "已过期 / 不可用";
        DesktopPalette.DrawText(e.Graphics, entry.Prefix + "… · " + state, message.Font, bounds, DesktopPalette.Muted, StringAlignment.Near);
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
        string previousId = keys.SelectedItem is AccountApiKey ? ((AccountApiKey)keys.SelectedItem).Id : null;
        userId = account; entries = values ?? new AccountApiKey[0];
        updating = true;
        keys.Items.Clear();
        keys.Items.AddRange(entries.Cast<object>().ToArray());
        int index = Array.FindIndex(entries, item => !changedUser && item.Id == previousId);
        if (index < 0) index = Array.FindIndex(entries, item => item.Usable);
        keys.SelectedIndex = index >= 0 ? index : entries.Length > 0 ? 0 : -1;
        updating = false;
        if (changedUser || !HasAccount) expanded = false;
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

    private void SelectKey()
    {
        if (updating || keys.SelectedItem == null) return;
        var entry = (AccountApiKey)keys.SelectedItem;
        string value = CachedKey(entry);
        SetMessage(!entry.Usable ? "此 Key 已停用、撤销或过期，请选择其他 Key。" : value == null
            ? "此 Key 尚无本机副本，请粘贴完整密钥；配置成功后可自动填入。" : "已填入所选 Key；清空后可手动填写。");
        if (KeyChosen != null) KeyChosen(value);
        if (value != null) Collapse();
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
