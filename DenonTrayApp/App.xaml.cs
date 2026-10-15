using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace DenonTrayApp
{
    public partial class App : Application
    {
        private NotifyIcon? _notifyIcon;
        private MainWindow? _mainWindow;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _mainWindow = new MainWindow();

            // Inicjalizacja ikony w zasobniku
            Icon icon = GetTrayIcon();

            _notifyIcon = new NotifyIcon
            {
                Icon = icon,
                Text = "Sterowanie głośnością Denon",
                Visible = true
            };

            // Kliknięcie lewym przyciskiem myszy otwiera flyout
            _notifyIcon.MouseClick += (s, args) =>
            {
                if (args.Button == MouseButtons.Left)
                {
                    var pos = System.Windows.Forms.Cursor.Position;
                    _mainWindow.ShowFlyout(pos);
                }
            };

            // Also handle MouseUp (some systems / configurations don't always send MouseClick reliably on first click)
            _notifyIcon.MouseUp += (s, args) =>
            {
                if (args.Button == MouseButtons.Left)
                {
                    var pos = System.Windows.Forms.Cursor.Position;
                    _mainWindow.ShowFlyout(pos);
                }
            };

            // Menu pod prawym przyciskiem myszy
            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Wyjście", null, (s, args) => ShutdownApp());
            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        private static Icon GetTrayIcon()
        {
            try
            {
                var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "speaker.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    return new Icon(iconPath);
                }
            }
            catch { }

            return CreateFallbackDIcon();
        }

        private static Icon CreateFallbackDIcon()
        {
            var bmp = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using var bg = new SolidBrush(Color.FromArgb(20, 28, 36));
            g.FillEllipse(bg, 0, 0, 15, 15);

            using var inner = new SolidBrush(Color.FromArgb(86, 170, 255));
            g.FillEllipse(inner, 2, 2, 11, 11);

            using var speakerBrush = new SolidBrush(Color.White);

            // speaker body
            g.FillRectangle(speakerBrush, 2, 6, 4, 5);

            var cone = new[]
            {
                new PointF(6, 5),
                new PointF(12, 2),
                new PointF(12, 14),
                new PointF(6, 11)
            };
            g.FillPolygon(speakerBrush, cone);

            // sound waves only, no crossed-out slash
            using var pen = new Pen(Color.White, 1.2f);
            g.DrawArc(pen, 11, 5, 7, 8, 210, 120);
            g.DrawArc(pen, 13, 3, 9, 12, 210, 120);

            var hIcon = bmp.GetHicon();
            return Icon.FromHandle(hIcon);
        }

        private void ShutdownApp()
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            Shutdown();
        }
    }
}