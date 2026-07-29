using System.Diagnostics;
using System.Drawing;
using SteamLoader.App.Models;
using Forms = System.Windows.Forms;

namespace SteamLoader.App.Infrastructure.Store;

internal sealed class StorePriceNotificationService
{
    private readonly SemaphoreSlim _displayGate = new(1, 1);

    public void Show(StorePriceAlertNotification notification, Action<string> openDeal)
    {
        var thread = new Thread(() => ShowCore(notification, openDeal))
        {
            IsBackground = true,
            Name = "TFS Store price notification"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void ShowCore(StorePriceAlertNotification notification, Action<string> openDeal)
    {
        _displayGate.Wait();
        try
        {
            Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
            using var form = new PriceNotificationForm(notification, openDeal);
            Forms.Application.Run(form);
        }
        catch
        {
            // A notification must never interrupt the background price watcher.
        }
        finally
        {
            _displayGate.Release();
        }
    }

    private sealed class PriceNotificationForm : Forms.Form
    {
        private const int ExtendedStyleNoActivate = 0x08000000;
        private const int ExtendedStyleToolWindow = 0x00000080;
        private readonly Stopwatch _lifetime = Stopwatch.StartNew();
        private readonly Forms.Timer _timer;

        public PriceNotificationForm(StorePriceAlertNotification notification, Action<string> openDeal)
        {
            AutoScaleMode = Forms.AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(12, 23, 30);
            ClientSize = new Size(560, 172);
            DoubleBuffered = true;
            FormBorderStyle = Forms.FormBorderStyle.None;
            Opacity = 0;
            ShowInTaskbar = false;
            StartPosition = Forms.FormStartPosition.Manual;
            TopMost = true;
            Cursor = Forms.Cursors.Hand;

            var accent = Color.FromArgb(94, 230, 168);
            var accentBar = new Forms.Panel
            {
                BackColor = accent,
                Dock = Forms.DockStyle.Left,
                Width = 7
            };
            var content = new Forms.TableLayoutPanel
            {
                BackColor = Color.Transparent,
                ColumnCount = 1,
                Dock = Forms.DockStyle.Fill,
                Padding = new Forms.Padding(24, 15, 20, 12),
                RowCount = 4
            };
            content.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
            content.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 23));
            content.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 38));
            content.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
            content.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 23));
            content.Controls.Add(CreateLabel("TOOLS FOR STEAM · PRICE ALERT", accent, 9.5f, FontStyle.Bold), 0, 0);
            content.Controls.Add(CreateLabel(notification.Title, Color.White, 14f, FontStyle.Bold), 0, 1);
            content.Controls.Add(CreateLabel(notification.Message, Color.FromArgb(204, 214, 222), 10f, FontStyle.Regular), 0, 2);
            content.Controls.Add(CreateLabel("Click to open this offer", accent, 9f, FontStyle.Bold), 0, 3);
            Controls.Add(content);
            Controls.Add(accentBar);

            var workingArea = Forms.Screen.PrimaryScreen?.WorkingArea ?? Forms.SystemInformation.VirtualScreen;
            if (Width > workingArea.Width - 32)
            {
                Width = Math.Max(320, workingArea.Width - 32);
            }
            Location = new Point(
                Math.Max(workingArea.Left + 16, workingArea.Right - Width - 24),
                workingArea.Top + 28);

            void activateOffer(object? sender, EventArgs eventArgs)
            {
                try { openDeal(notification.DealUrl); } catch { }
                Close();
            }
            Click += activateOffer;
            AttachClick(content, activateOffer);
            accentBar.Click += activateOffer;

            _timer = new Forms.Timer { Interval = 25 };
            _timer.Tick += (_, _) => AnimateNotification();
            Shown += (_, _) =>
            {
                TopMost = true;
                _lifetime.Restart();
                _timer.Start();
            };
            FormClosed += (_, _) => _timer.Dispose();
        }

        protected override bool ShowWithoutActivation => true;

        protected override Forms.CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= ExtendedStyleNoActivate | ExtendedStyleToolWindow;
                return parameters;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyRoundedRegion();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (IsHandleCreated) ApplyRoundedRegion();
        }

        private static Forms.Label CreateLabel(string text, Color color, float size, FontStyle style)
        {
            return new Forms.Label
            {
                AutoEllipsis = true,
                AutoSize = false,
                Cursor = Forms.Cursors.Hand,
                Dock = Forms.DockStyle.Fill,
                ForeColor = color,
                Font = new Font("Segoe UI", size, style),
                Margin = Forms.Padding.Empty,
                Text = text
            };
        }

        private static void AttachClick(Forms.Control parent, EventHandler handler)
        {
            parent.Click += handler;
            foreach (Forms.Control child in parent.Controls)
            {
                child.Click += handler;
                if (child.HasChildren) AttachClick(child, handler);
            }
        }

        private void ApplyRoundedRegion()
        {
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            const int radius = 20;
            path.AddArc(0, 0, radius, radius, 180, 90);
            path.AddArc(Width - radius, 0, radius, radius, 270, 90);
            path.AddArc(Width - radius, Height - radius, radius, radius, 0, 90);
            path.AddArc(0, Height - radius, radius, radius, 90, 90);
            path.CloseFigure();
            var previous = Region;
            Region = new Region(path);
            previous?.Dispose();
        }

        private void AnimateNotification()
        {
            var elapsed = _lifetime.Elapsed.TotalMilliseconds;
            if (elapsed < 220) Opacity = Math.Min(1, elapsed / 220);
            else if (elapsed < 8500) Opacity = 1;
            else if (elapsed < 9000) Opacity = Math.Max(0, 1 - ((elapsed - 8500) / 500));
            else
            {
                _timer.Stop();
                Close();
            }
        }
    }
}
