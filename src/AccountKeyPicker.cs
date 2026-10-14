using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

// Only the selected, successfully used full key is cached. Website metadata
// cannot recover plaintext: it must never be treated as a model credential.
internal sealed class AccountKeyPicker : Panel
{
    private readonly Button toggle;
    private readonly ComboBox keys;
    private readonly Label message;
    private readonly string directory;
    private string userId;
    private AccountApiKey[] entries = new AccountApiKey[0];
    private bool expanded, updating;
    internal event Action HeightChanged;
    internal event Action<string> KeyChosen;
    internal bool HasAccount { get { return userId != null; } }
    internal int LogicalHeight { get { return HasAccount ? (expanded ? 100 : 28) : 0; } }

    internal AccountKeyPicker(string directory)
    {
        this.directory = directory;
        Font = new Font("Segoe UI", 13F, FontStyle.Regular, GraphicsUnit.Pixel);
        Margin = Padding.Empty; Dock = DockStyle.Fill;
        toggle = new DesktopButton { Text = "展开账户 API Key", Borderless = true, ForeColor = DesktopPalette.Text,
            AccessibleName = "展开或收起账户 API Key 列表", Font = new Font("Segoe UI", 11F, FontStyle.Regular, GraphicsUnit.Pixel) };
        keys = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, IntegralHeight = false, MaxDropDownItems = 8,
            DropDownHeight = 200, AccessibleName = "选择已登录账户的 API Key", Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Pixel) };
        message = new Label { ForeColor = DesktopPalette.Muted, UseMnemonic = false, AutoEllipsis = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Pixel) };
        Controls.AddRange(new Control[] { toggle, keys, message });
        toggle.Click += delegate { expanded = !expanded; RefreshLayout(); if (HeightChanged != null) HeightChanged(); };
        keys.SelectionChangeCommitted += delegate { SelectKey(); };
        Resize += delegate { RefreshLayout(); };
        Visible = false;
    }

    private int Pixels(int value) { return (int)Math.Round(value * Font.Size / 13F); }
    private void RefreshLayout()
    {
        toggle.SetBounds(0, 0, ClientSize.Width, Pixels(26));
        toggle.Text = (expanded ? "收起" : "展开") + "账户 API Key（" + entries.Length + "）";
        keys.Visible = message.Visible = expanded && HasAccount;
        keys.SetBounds(0, Pixels(30), ClientSize.Width, Pixels(28));
        message.SetBounds(0, Pixels(62), ClientSize.Width, Pixels(34));
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
        if (changedUser) expanded = account != null;
        Visible = HasAccount;
        message.Text = error ?? (entries.Length == 0 ? "此账户暂无 API Key，可继续手动粘贴。" : "选择 Key；首次使用需粘贴完整密钥，成功配置后可自动填入。");
        RefreshLayout();
        if (HeightChanged != null) HeightChanged();
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
        message.Text = !entry.Usable ? "此 Key 已停用、撤销或过期，请选择其他 Key。" : value == null
            ? "官网不返回已有完整 Key，请粘贴此 Key；成功配置后会加密记住。" : "已从本机加密副本填入，可继续手动编辑。";
        if (KeyChosen != null) KeyChosen(value);
    }

    internal string AvailableKey(string existing)
    {
        if (!HasAccount) return null;
        foreach (var entry in entries)
        {
            if (Matches(entry, existing)) { keys.SelectedItem = entry; return existing; }
            string cached = CachedKey(entry);
            if (cached != null) { keys.SelectedItem = entry; return cached; }
        }
        return null;
    }

    internal void Remember(string secret)
    {
        if (!HasAccount) return;
        var entry = entries.FirstOrDefault(item => Matches(item, secret));
        if (entry == null) return;
        byte[] plain = PrivateFiles.Utf8.GetBytes(secret);
        try { PrivateFiles.WriteAtomic(KeyPath(entry), PrivateFiles.Protect(plain)); message.Text = "已加密记住此 Key，下次可从列表选择。"; }
        catch { message.Text = "配置成功，但账户 Key 副本保存失败；下次可手动粘贴。"; }
        finally { Array.Clear(plain, 0, plain.Length); }
    }
}
