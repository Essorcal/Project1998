// The WebView2 shell: one native window hosting the editor UI, so double-clicking the exe
// gives a single self-contained application — no console window, no browser tab, and
// closing the window shuts the whole thing down. The web frontend is unchanged: WebView2
// IS Chromium (Edge), so rendering, display-scale behavior, and even F12 devtools are
// identical to the browser the editor grew up in.
using System.Drawing.Drawing2D;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MapEditor;

public static class AppWindow
{
    /// <summary>Show the editor window and block until it closes. False = the WebView2
    /// runtime is missing (caller falls back to the system browser).</summary>
    public static bool Run(string url)
    {
        try { _ = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (WebView2RuntimeNotFoundException) { return false; }

        // WinForms wants an STA thread, which top-level Main is not.
        var t = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.Run(new EditorForm(url));
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return true;
    }

    static string StateDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Project1998");

    sealed class EditorForm : Form
    {
        readonly string _url;
        readonly string _boundsFile = Path.Combine(StateDir, "MapEditor.window.json");

        public EditorForm(string url)
        {
            _url = url;
            Text = "NexusTK Map Editor";
            Icon = MakeIcon();
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1500, 950);
            MinimumSize = new Size(900, 600);
            RestoreBounds_();
            Load += OnLoad;
            FormClosing += (_, _) => SaveBounds();
        }

        async void OnLoad(object? sender, EventArgs e)
        {
            var wv = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(wv);
            try
            {
                // Own data folder: keeps localStorage (favorites, pending placements) stable
                // across exe moves, and avoids writing next to an exe in a read-only spot.
                var env = await CoreWebView2Environment.CreateAsync(null,
                    Path.Combine(StateDir, "MapEditor.WebView2"));
                await wv.EnsureCoreWebView2Async(env);
                wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                wv.CoreWebView2.Navigate(_url);
            }
            catch (Exception ex)
            {
                MessageBox.Show("The embedded browser failed to start:\n\n" + ex.Message +
                    "\n\nRun with --browser to use your normal browser instead.",
                    "NexusTK Map Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        // Sanity-clamped restore: a remembered position on a monitor that no longer exists
        // must not open the window off-screen.
        void RestoreBounds_()
        {
            try
            {
                var s = JsonSerializer.Deserialize<WindowState_>(File.ReadAllText(_boundsFile));
                if (s is null) return;
                var r = new Rectangle(s.X, s.Y, Math.Max(s.W, 900), Math.Max(s.H, 600));
                if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r)))
                {
                    StartPosition = FormStartPosition.Manual;
                    Bounds = r;
                }
                if (s.Max) WindowState = FormWindowState.Maximized;
            }
            catch { /* first run, or an unreadable file — defaults are fine */ }
        }

        void SaveBounds()
        {
            try
            {
                Directory.CreateDirectory(StateDir);
                var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                File.WriteAllText(_boundsFile, JsonSerializer.Serialize(new WindowState_(
                    r.X, r.Y, r.Width, r.Height, WindowState == FormWindowState.Maximized)));
            }
            catch { }
        }

        record WindowState_(int X, int Y, int W, int H, bool Max);

        // The README's amber grid glyph, drawn at runtime so no .ico ships in the repo.
        static Icon MakeIcon()
        {
            using var bmp = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Color.FromArgb(217, 150, 46)))
            using (var path = RoundedRect(new Rectangle(1, 1, 30, 30), 6))
                g.FillPath(bg, path);
            using var pen = new Pen(Color.FromArgb(23, 24, 26), 2);
            g.DrawRectangle(pen, 6, 6, 20, 20);
            g.DrawLine(pen, 6, 13, 26, 13);
            g.DrawLine(pen, 6, 19, 26, 19);
            g.DrawLine(pen, 13, 6, 13, 26);
            g.DrawLine(pen, 19, 6, 19, 26);
            return Icon.FromHandle(bmp.GetHicon());
        }

        static GraphicsPath RoundedRect(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, rad, rad, 180, 90);
            p.AddArc(r.Right - rad, r.Y, rad, rad, 270, 90);
            p.AddArc(r.Right - rad, r.Bottom - rad, rad, rad, 0, 90);
            p.AddArc(r.X, r.Bottom - rad, rad, rad, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
