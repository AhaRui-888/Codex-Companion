using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class DesktopUiTests
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        string data = Path.Combine(root, "isolated-settings");
        string codex = Path.Combine(root, "isolated-codex");
        using (var form = new MainForm(new AppController(data, codex), true, new PreviewThemeGalleryService()))
        {
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Show();
            Application.DoEvents();
            var key = Field<TextBox>(form, "apiKeyTextBox");
            var apply = Field<Button>(form, "applyButton");
            var appearance = Field<Button>(form, "appearanceButton");
            var update = Field<Button>(form, "updateButton");
            var eye = Field<Button>(form, "eyeButton");
            Assert(!apply.Enabled && appearance.Enabled, "Preview must prevent system configuration and allow appearance browsing.");
            Assert(!update.Enabled, "Preview must not check GitHub for updates.");
            Assert(key.UseSystemPasswordChar, "API Key must start masked.");
            key.Text = "sk-ui-fixture-not-a-real-key";
            eye.PerformClick();
            Assert(!key.UseSystemPasswordChar && eye.Text == "隐藏", "Reveal button must retain its action.");
            eye.PerformClick();
            Assert(key.UseSystemPasswordChar && eye.Text == "显示", "Reveal button must mask again.");
            key.Clear();
            ((Task)Call(form, "ApplyAsync")).GetAwaiter().GetResult();
            Assert(Field<Label>(form, "statusLabel").Text.Contains("请输入"), "Empty credentials must still fail before configuration.");

            Call(form, "SetBusy", true, "正在验证界面状态…");
            Assert(!appearance.Enabled && !Field<Button>(form, "officialWebsiteButton").Enabled, "Busy actions must stay blocked.");
            Call(form, "SetBusy", false, null);
            Assert(appearance.Enabled && !apply.Enabled, "Preview restrictions must survive busy-state changes.");

            // Demonstrate the enabled presentation without executing any action.
            Call(form, "SetOperationsEnabled", true);
            Call(form, "SetStatus", "界面效果预览 · 使用隔离测试数据，未执行配置或模型调用。");
            CheckLayout(form);
            SaveClient(form, Path.Combine(root, "desktop-default.png"));
            var account = Field<AccountPanel>(form, "accountPanel");
            foreach (Control link in Field<FlowLayoutPanel>(form, "headerLinks").Controls)
                Assert(!link.Text.Contains("充值"), "Recharge must only be offered in the account panel.");
            Assert(Field<Button>(account, "rechargeButton").Visible && !Field<Button>(account, "rechargeButton").Enabled,
                "Recharge must be disabled before login.");
            Assert(Field<Button>(account, "loginButton").Visible && !Field<Button>(account, "loginButton").Enabled,
                "Signed-out preview must show login without opening a real login session.");
            account.SetPreviewSnapshot(new AccountSnapshot { AccountName = "fixture@example.test", UserId = "fixture-user",
                Balance = 123.456789M, Reserved = 2.345678M, UpdatedAt = new DateTime(2026, 10, 10, 12, 30, 0) });
            Application.DoEvents();
            CheckLayout(form);
            SaveClient(form, Path.Combine(root, "desktop-account.png"));
            float fixedFontSize = apply.Font.Size;
            form.Size = form.MinimumSize;
            Application.DoEvents();
            CheckLayout(form);
            SaveClient(form, Path.Combine(root, "desktop-minimum.png"));
            var viewport = Field<Panel>(form, "layoutViewport");
            Assert(Field<bool>(form, "compactHeader") && Field<bool>(form, "compactActions"),
                "Narrow windows must move header links and secondary actions to their own rows.");
            Assert(viewport.VerticalScroll.Visible, "Short narrow windows must offer native scrolling for every action.");
            viewport.ScrollControlIntoView(Field<Button>(form, "restoreButton"));
            Application.DoEvents();
            Assert(form.ClientRectangle.Contains(form.RectangleToClient(Field<Button>(form, "restoreButton").RectangleToScreen(Field<Button>(form, "restoreButton").ClientRectangle))),
                "Restore must remain reachable through the short window's scrollbar.");
            SaveClient(form, Path.Combine(root, "desktop-short-scrolled.png"));
            form.ClientSize = new Size(660, 700);
            Application.DoEvents();
            CheckLayout(form);
            Assert(!viewport.VerticalScroll.Visible, "A tall narrow window must fit the reflowed controls without scrolling.");
            SaveClient(form, Path.Combine(root, "desktop-narrow.png"));
            form.SetAdvertisements(new[] { new Advertisement("平台推广 · 支持长内容、键盘切换和独立详情。", AppController.OfficialWebsiteUrl) });
            Application.DoEvents();
            CheckLayout(form);
            SaveClient(form, Path.Combine(root, "desktop-advertisement.png"));
            form.SetAdvertisements(new Advertisement[0]);
            form.ClientSize = new Size(1240, 860);
            Application.DoEvents();
            CheckLayout(form);
            SaveClient(form, Path.Combine(root, "desktop-expanded.png"));
            var content = Field<TableLayoutPanel>(form, "rootLayout");
            Assert(content.Top > 120 && Math.Abs(content.Top - (viewport.Height - content.Bottom)) <= 2,
                "Tall windows must balance space around the complete content group instead of expanding a blank status row.");
            form.ClientSize = new Size(1600, 900);
            Application.DoEvents();
            CheckLayout(form);
            Assert(content.Width <= 1230 && Math.Abs(content.Left - (viewport.Width - content.Right)) <= 2,
                "Wide windows must preserve a centered readable content column.");
            Assert(apply.Font.Size == fixedFontSize, "Resizing the window must preserve readable fixed font sizes.");
            SaveClient(form, Path.Combine(root, "desktop-wide.png"));
            Call(form, "SetStatus", "配置失败：" + new string('文', 140));
            Application.DoEvents();
            Assert(Field<Label>(form, "statusLabel").Height > 60, "Long operation messages must grow and wrap rather than being clipped.");
            CheckLayout(form);
            Assert(!Directory.Exists(data) && !Directory.Exists(codex), "UI-only interactions must not write configuration.");
            key.Text = "sk-manually-pasted-fixture";
            Call(form, "SetStatus", "界面效果预览 · API Key 列表使用隔离数据。");
            Call(form, "UpdateAccountKeys", "fixture-user", new[] {
                new AccountApiKey { Id = "one", Name = "工作项目", Prefix = "sk-work-", Status = "ACTIVE" },
                new AccountApiKey { Id = "two", Name = "个人项目", Prefix = "sk-personal-", Status = "ACTIVE" } }, null);
            Assert(key.Text == "sk-manually-pasted-fixture", "loading account keys must preserve manually pasted input");
            var picker = Field<AccountKeyPicker>(form, "accountKeyPicker");
            foreach (var size in new[] { new Size(930, 700), new Size(660, 820) })
            {
                form.ClientSize = size;
                Application.DoEvents();
                CheckLayout(form);
                foreach (Control control in picker.Controls)
                    if (control.Visible) Assert(picker.ClientRectangle.Contains(control.Bounds), "expanded account key controls must fit at all supported widths");
                SaveClient(form, Path.Combine(root, "desktop-keys-" + size.Width + ".png"));
            }
            Field<Button>(picker, "toggle").PerformClick();
            Assert(picker.LogicalHeight == 28, "account key list must collapse");
            Call(form, "UpdateAccountKeys", null, new AccountApiKey[0], null);
            Assert(!picker.Visible && key.Text == "sk-manually-pasted-fixture", "logout clears the key list while preserving independent manual input");
            form.Close();
        }
        using (var gallery = new ThemeGalleryForm(new AppController(data, codex), new PreviewThemeGalleryService(), true))
        {
            gallery.ShowInTaskbar = false;
            gallery.Opacity = 0;
            gallery.Show();
            var ready = System.Diagnostics.Stopwatch.StartNew();
            while (!gallery.ContentReady.IsCompleted && ready.ElapsedMilliseconds < 5000)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(10);
            }
            var tabs = Field<TabControl>(gallery, "appearanceTabs");
            var theme = Field<ComboBox>(gallery, "themeComboBox");
            var skin = Field<ComboBox>(gallery, "skinComboBox");
            Assert(!Field<Button>(gallery, "appearanceApplyButton").Enabled, "Preview must prevent appearance writes.");
            theme.SelectedValue = "codex-dark";
            skin.SelectedValue = "qq";
            Assert(Convert.ToString(theme.SelectedValue) == "codex-dark", "Theme must retain its ID.");
            Assert(Convert.ToString(skin.SelectedValue) == "qq", "Skin must retain its ID.");
            theme.Focus();
            theme.DroppedDown = true;
            Application.DoEvents();
            Assert(theme.DroppedDown, "Appearance styling must preserve the native dropdown.");
            theme.DroppedDown = false;
            CheckAppearanceLayout(gallery);
            SaveClient(gallery, Path.Combine(root, "appearance.png"));

            tabs.SelectedIndex = 1;
            Application.DoEvents();
            var cards = Field<FlowLayoutPanel>(gallery, "cardsPanel");
            Assert(cards.Controls.Count > 0, "Gallery cards must be populated.");
            CheckGalleryLayout(gallery);
            SaveClient(gallery, Path.Combine(root, "gallery.png"));
            gallery.Size = gallery.MinimumSize;
            Application.DoEvents();
            CheckGalleryLayout(gallery);
            SaveClient(gallery, Path.Combine(root, "gallery-minimum.png"));
            gallery.Close();
        }
        Assert(!Directory.Exists(data) && !Directory.Exists(codex), "UI-only interactions must not write configuration.");
        Console.WriteLine("Desktop UI: separated appearance dialog, credential masking, native popup, busy-state locks, layouts, advertisements and gallery passed.");
    }

    private static void CheckLayout(MainForm form)
    {
        var root = Field<TableLayoutPanel>(form, "rootLayout");
        var viewport = Field<Panel>(form, "layoutViewport");
        foreach (string field in new[] { "apiKeyTextBox", "eyeButton", "appearanceButton", "applyButton", "openCodexButton", "restoreButton", "officialWebsiteButton", "updateButton" })
        {
            var control = Field<Control>(form, field);
            Assert(control.Visible && control.Width > 24 && control.Height >= 18, field + " must remain visible and usable.");
            Rectangle bounds = root.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
            Assert(root.ClientRectangle.Contains(bounds), field + " must fit the content at the current window size.");
            Assert(control.Parent.ClientRectangle.Contains(control.Bounds), field + " must not be clipped by its parent.");
            if (!viewport.VerticalScroll.Visible)
                Assert(form.ClientRectangle.Contains(form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle))),
                    field + " must be visible without scrolling when the content fits.");
            if (control is DesktopButton)
            {
                using (var graphics = control.CreateGraphics())
                {
                    SizeF text = graphics.MeasureString(control.Text, control.Font);
                    Assert(text.Width <= control.Width - 12 && text.Height <= control.Height, field + " must display its complete action label.");
                }
            }
        }
        Control[] brands = form.Controls.Find("brandLabel", true);
        Assert(brands.Length == 1 && brands[0].Visible && brands[0].Height >= 18,
            "Branding must remain visible in the compact header.");
        using (var graphics = brands[0].CreateGraphics())
            Assert(graphics.MeasureString(brands[0].Text, brands[0].Font).Width <= brands[0].Width,
                "The compact header must display the complete title.");
        Label domain = FindLabel(form, new Uri(AppController.OfficialRelayUrl).Host);
        Assert(form.Text == "codex官方中转站" && brands[0].Text == "codex官方中转站" && domain != null && domain.Visible,
            "Window title, header title and official domain must remain accurate.");
        using (var graphics = domain.CreateGraphics())
            Assert(graphics.MeasureString(domain.Text, domain.Font).Width <= domain.Width,
                "The compact header must display the complete official domain.");
        TextBox key = Field<TextBox>(form, "apiKeyTextBox");
        Control keyFrame = key.Parent;
        Rectangle keyBounds = form.RectangleToClient(keyFrame.RectangleToScreen(keyFrame.ClientRectangle));
        Rectangle applyBounds = form.RectangleToClient(Field<Button>(form, "applyButton").RectangleToScreen(Field<Button>(form, "applyButton").ClientRectangle));
        Rectangle eyeBounds = form.RectangleToClient(Field<Button>(form, "eyeButton").RectangleToScreen(Field<Button>(form, "eyeButton").ClientRectangle));
        Assert(Math.Abs(keyBounds.Top - applyBounds.Top) <= 2 && Math.Abs(keyBounds.Bottom - applyBounds.Bottom) <= 2 && applyBounds.Left > eyeBounds.Right,
            "The API key and primary action must share one compact row.");
        Rectangle appearanceBounds = form.RectangleToClient(Field<Button>(form, "appearanceButton").RectangleToScreen(Field<Button>(form, "appearanceButton").ClientRectangle));
        Rectangle updateBounds = form.RectangleToClient(Field<Button>(form, "updateButton").RectangleToScreen(Field<Button>(form, "updateButton").ClientRectangle));
        Assert(Math.Abs(appearanceBounds.Top - updateBounds.Top) <= 2 && appearanceBounds.Bottom < keyBounds.Top,
            "Appearance settings must stay in the top action bar.");
        Assert(!viewport.HorizontalScroll.Visible, "Supported sizes must not need horizontal scrolling: form=" + form.ClientSize
            + ", viewport=" + viewport.ClientSize + ", root=" + root.Bounds + ", scroll=" + viewport.AutoScrollPosition
            + ", display=" + viewport.DisplayRectangle + ", minimum=" + viewport.AutoScrollMinSize);
        Rectangle brandBounds = root.RectangleToClient(brands[0].RectangleToScreen(brands[0].ClientRectangle));
        Rectangle domainBounds = root.RectangleToClient(domain.RectangleToScreen(domain.ClientRectangle));
        Rectangle headerActionBounds = root.RectangleToClient(Field<Button>(form, "appearanceButton").RectangleToScreen(Field<Button>(form, "appearanceButton").ClientRectangle));
        Assert(!brandBounds.IntersectsWith(domainBounds) && !brandBounds.IntersectsWith(headerActionBounds) && !domainBounds.IntersectsWith(headerActionBounds),
            "Header branding, domain and actions must never overlap.");
        var hint = Field<Label>(form, "credentialHintLabel");
        using (var graphics = hint.CreateGraphics())
            Assert(graphics.MeasureString(hint.Text, hint.Font, hint.Width - hint.Padding.Horizontal).Height <= hint.Height - hint.Padding.Vertical + 1,
                "Credential guidance must have enough height to wrap at narrow widths.");
        var account = Field<AccountPanel>(form, "accountPanel");
        foreach (string field in new[] { "balanceLabel", "accountLabel", "accountStatusLabel", "loginButton", "rechargeButton", "refreshBalanceButton", "logoutButton" })
        {
            var control = Field<Control>(account, field);
            if (!control.Visible) continue;
            Assert(account.ClientRectangle.Contains(control.Bounds), "Account " + field + " must fit its panel at every supported width.");
            if (control is Button)
                using (var graphics = control.CreateGraphics())
                    Assert(graphics.MeasureString(control.Text, control.Font).Width <= control.Width - 12,
                        "Account " + field + " must retain its complete action label.");
        }
        var balance = Field<Label>(account, "balanceLabel");
        var recharge = Field<Button>(account, "rechargeButton");
        Assert(!balance.Bounds.IntersectsWith(recharge.Bounds), "Recharge must never overlap the balance.");
        using (var graphics = balance.CreateGraphics())
            Assert(graphics.MeasureString(balance.Text, balance.Font).Width <= balance.Width,
                "The balance must remain readable beside or above recharge actions.");
    }
    private static void CheckAppearanceLayout(ThemeGalleryForm form)
    {
        foreach (string field in new[] { "themeComboBox", "skinComboBox", "appearanceApplyButton", "appearanceCloseButton", "appearanceStatusLabel" })
        {
            Control control = Field<Control>(form, field);
            Rectangle bounds = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
            Assert(control.Visible && control.Width > 24 && control.Height >= 18 && form.ClientRectangle.Contains(bounds),
                field + " must remain visible within the appearance dialog.");
        }
        Control preview = Field<Control>(form, "themePreviewPanel");
        Assert(preview.Visible && preview.Width >= 120 && preview.Height >= 60, "Theme preview must remain visible and readable.");
        Assert(Field<ComboBox>(form, "themeComboBox").Width >= 190, "Theme names need a readable selector.");
    }
    private static void CheckGalleryLayout(ThemeGalleryForm form)
    {
        foreach (string field in new[] { "searchBox", "availabilityComboBox", "previousPageButton", "nextPageButton", "restoreButton", "closeButton", "pageLabel" })
        {
            var control = Field<Control>(form, field);
            Rectangle bounds = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
            Assert(control.Visible && form.ClientRectangle.Contains(bounds), field + " must remain within the gallery.");
            if (control is DesktopButton)
                using (var graphics = control.CreateGraphics())
                    Assert(graphics.MeasureString(control.Text, control.Font).Width <= control.Width - 12,
                        field + " must display its complete gallery action.");
        }
        Control card = Field<FlowLayoutPanel>(form, "cardsPanel").Controls[0];
        foreach (string name in new[] { "title", "meta", "summary", "status" })
        {
            var label = Field<Label>(card, name);
            Assert(label.Visible && card.ClientRectangle.Contains(label.Bounds), "Gallery card " + name + " must fit.");
            using (var graphics = label.CreateGraphics())
                Assert(graphics.MeasureString("文字", label.Font).Height <= label.Height, "Gallery card " + name + " must have readable text height.");
        }
    }
    private static void SaveClient(Form form, string path)
    {
        using (var full = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(full, new Rectangle(Point.Empty, full.Size));
            Point client = form.PointToScreen(Point.Empty);
            using (var image = full.Clone(new Rectangle(client.X - form.Left, client.Y - form.Top, form.ClientSize.Width, form.ClientSize.Height), full.PixelFormat))
                image.Save(path);
        }
    }
    private static T Field<T>(object instance, string name)
    { return (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance); }
    private static object Call(object instance, string name, params object[] arguments)
    { return instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, arguments); }
    private static Label FindLabel(Control root, string text)
    {
        foreach (Control control in root.Controls)
        {
            Label label = control as Label;
            if (label != null && label.Text == text) return label;
            Label nested = FindLabel(control, text);
            if (nested != null) return nested;
        }
        return null;
    }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
