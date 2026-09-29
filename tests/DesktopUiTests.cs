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
            var theme = Field<ComboBox>(form, "themeComboBox");
            var skin = Field<ComboBox>(form, "skinComboBox");
            var pet = Field<ComboBox>(form, "petComboBox");
            var apply = Field<Button>(form, "applyButton");
            var gallery = Field<Button>(form, "galleryButton");
            var update = Field<Button>(form, "updateButton");
            var eye = Field<Button>(form, "eyeButton");
            Assert(!apply.Enabled && gallery.Enabled, "Preview must prevent configuration and allow gallery browsing.");
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

            theme.SelectedValue = "codex-dark";
            skin.SelectedValue = "qq";
            pet.SelectedValue = "juniper";
            Assert((string)Call(form, "SelectedValueOrText", theme, "keep") == "codex-dark", "Theme must submit its ID, not its label.");
            Assert((string)Call(form, "SelectedValueOrText", skin, "native") == "qq", "Skin binding must retain its ID.");
            Assert((string)Call(form, "SelectedValueOrText", pet, "keep") == "juniper", "Pet binding must retain its ID.");
            theme.Focus();
            theme.DroppedDown = true;
            Application.DoEvents();
            Assert(theme.DroppedDown, "Custom styling must preserve the native dropdown.");
            theme.DroppedDown = false;
            Call(form, "SetBusy", true, "正在验证界面状态…");
            Assert(!gallery.Enabled && !Field<Button>(form, "officialWebsiteButton").Enabled, "Busy actions must stay blocked.");
            Call(form, "SetBusy", false, null);
            Assert(gallery.Enabled && !apply.Enabled, "Preview restrictions must survive busy-state changes.");

            // Demonstrate the enabled presentation without executing any action.
            Call(form, "SetOperationsEnabled", true);
            skin.SelectedValue = "native";
            pet.SelectedValue = "keep";
            Call(form, "SetStatus", "界面效果预览 · 使用隔离测试数据，未执行配置或模型调用。");
            CheckLayout(form);
            SaveClient(form, Path.Combine(root, "desktop-default.png"));
            form.Size = form.MinimumSize;
            Application.DoEvents();
            CheckLayout(form);
            SaveClient(form, Path.Combine(root, "desktop-minimum.png"));
            form.SetAdvertisements(new[] { new Advertisement("平台推广 · 支持长内容、键盘切换和独立详情。", AppController.OfficialWebsiteUrl) });
            Application.DoEvents();
            CheckLayout(form);
            SaveClient(form, Path.Combine(root, "desktop-advertisement.png"));
            form.SetAdvertisements(new Advertisement[0]);
            form.ClientSize = new Size(1240, 860);
            Application.DoEvents();
            CheckLayout(form);
            SaveClient(form, Path.Combine(root, "desktop-expanded.png"));
            Assert(!Directory.Exists(data) && !Directory.Exists(codex), "UI-only interactions must not write configuration.");
            form.Close();
        }
        using (var gallery = new ThemeGalleryForm(new PreviewThemeGalleryService(), true))
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
        Console.WriteLine("Desktop UI: credential masking, empty-key guard, selection IDs, native popup, busy-state locks, default/minimum/expanded layouts, advertisements and gallery passed.");
    }

    private static void CheckLayout(MainForm form)
    {
        foreach (string field in new[] { "apiKeyTextBox", "eyeButton", "themeComboBox", "skinComboBox", "petComboBox", "galleryButton", "applyButton", "openCodexButton", "restoreButton", "officialWebsiteButton", "rechargeButton", "updateButton" })
        {
            var control = Field<Control>(form, field);
            Assert(control.Visible && control.Width > 24 && control.Height >= 18, field + " must remain visible and usable.");
            Rectangle bounds = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
            Assert(form.ClientRectangle.Contains(bounds), field + " must not be clipped at the current window size.");
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
        Assert(brands.Length == 1 && brands[0].Visible && brands[0].Width >= 200 && brands[0].Height >= 24,
            "Codex Companion branding must remain visible in the header.");
        Assert(form.Text == "Codex Companion" && brands[0].Text == "Codex Companion", "Window and header must use the requested app name.");
        var preview = Field<Control>(form, "themePreviewPanel");
        Assert(preview.ClientRectangle.Contains(new Rectangle(12, 10, 73, 57)), "The entire painted theme preview must fit inside its surface.");
        Assert(preview.ClientRectangle.Contains(Field<Button>(form, "galleryButton").Bounds), "Gallery action must fit inside the theme preview.");
        for (Control current = preview; current.Parent != null; current = current.Parent)
            Assert(current.Parent.ClientRectangle.Contains(current.Bounds), "Theme preview ancestors must not clip their children.");
        Assert(Field<ComboBox>(form, "themeComboBox").Width >= 190, "Theme names need a readable selector.");
        var root = Field<TableLayoutPanel>(form, "rootLayout");
        Assert(!root.HorizontalScroll.Visible, "Supported sizes must not need horizontal scrolling.");
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
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
