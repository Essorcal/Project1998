// The WebView2 shell, same as MapEditor's: one native window hosting the UI so double-clicking
// the exe gives a self-contained application, and closing the window shuts everything down.
using System.Drawing.Drawing2D;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace IconStudio;

public static class AppWindow
{
    /// <summary>Show the window and block until it closes. False = the WebView2 runtime is
    /// missing (the caller falls back to the system browser).</summary>
    public static bool Run(string url)
    {
        try { _ = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (WebView2RuntimeNotFoundException) { return false; }

        var t = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.Run(new StudioForm(url));
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return true;
    }

    static string StateDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Project1998");

    sealed class StudioForm : Form
    {
        readonly string _url;
        readonly string _boundsFile = Path.Combine(StateDir, "IconStudio.window.json");

        public StudioForm(string url)
        {
            _url = url;
            Text = "NexusTK Icon Studio";
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
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(StateDir, "IconStudio.WebView2"));
                await wv.EnsureCoreWebView2Async(env);
                wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                wv.CoreWebView2.Navigate(_url);
            }
            catch (Exception ex)
            {
                MessageBox.Show("The embedded browser failed to start:\n\n" + ex.Message +
                    "\n\nRun with --browser to use your normal browser instead.",
                    "NexusTK Icon Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

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
            catch { }
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

        // A jade tile with a four-pixel glyph — drawn at runtime so no .ico ships in the repo.
        static Icon MakeIcon()
        {
            using var bmp = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Color.FromArgb(63, 168, 122)))
            using (var path = RoundedRect(new Rectangle(1, 1, 30, 30), 6))
                g.FillPath(bg, path);
            using var dark = new SolidBrush(Color.FromArgb(23, 24, 26));
            g.FillRectangle(dark, 7, 7, 8, 8);
            g.FillRectangle(dark, 17, 7, 8, 8);
            g.FillRectangle(dark, 7, 17, 8, 8);
            using var light = new SolidBrush(Color.FromArgb(232, 234, 236));
            g.FillRectangle(light, 17, 17, 8, 8);
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
