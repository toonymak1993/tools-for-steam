using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace SteamLoader.App.Infrastructure.Handheld;

internal sealed class WindowsProfileNotificationService
{
    private readonly SemaphoreSlim _displayGate = new(1, 1);
    private readonly string _logPath;

    public WindowsProfileNotificationService(string dataDirectory)
    {
        _logPath = Path.Combine(dataDirectory, "handheld-profile-notifications.log");
    }

    public void ShowProfileApplied(HandheldAutomaticProfileResult result)
    {
        if (!result.ShowNotification)
        {
            Log($"notification skipped disabled title={result.Title} watts={result.TdpWatts}");
            return;
        }

        Log($"notification queued title={result.Title} watts={result.TdpWatts} gameProfile={result.IsGameProfile}");
        var thread = new Thread(() => ShowNotification(result))
        {
            IsBackground = true,
            Name = "TFS Performance notification"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void ShowNotification(HandheldAutomaticProfileResult result)
    {
        _displayGate.Wait();
        try
        {
            Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
            using var notification = new ProfileNotificationForm(result);
            Log($"notification window showing bounds={notification.Bounds}");
            Forms.Application.Run(notification);
            Log("notification window closed");
        }
        catch (Exception exception)
        {
            Log($"notification failed type={exception.GetType().Name} message={exception.Message}");
        }
        finally
        {
            _displayGate.Release();
        }
    }

    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(
                _logPath,
                $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private sealed class ProfileNotificationForm : Forms.Form
    {
        private const int ExtendedStyleNoActivate = 0x08000000;
        private const int ExtendedStyleToolWindow = 0x00000080;
        private readonly Stopwatch _lifetime = Stopwatch.StartNew();
        private readonly Forms.Timer _animationTimer;

        public ProfileNotificationForm(HandheldAutomaticProfileResult result)
        {
            var isRestoredGlobal = !result.IsGameProfile &&
                string.Equals(result.Title, "Global profile", StringComparison.OrdinalIgnoreCase);
            var title = result.IsGameProfile
                ? "Game profile applied"
                : isRestoredGlobal
                    ? "Global profile restored"
                    : "Global profile active";
            var detail = result.IsGameProfile
                ? $"{result.Title} is now using {result.TdpWatts} W."
                : isRestoredGlobal
                    ? $"The default {result.TdpWatts} W profile is active again."
                    : $"No game profile for {result.Title}. Using the global {result.TdpWatts} W profile.";
            var accent = result.IsGameProfile
                ? Color.FromArgb(77, 205, 137)
                : Color.FromArgb(247, 166, 66);

            AutoScaleMode = Forms.AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(20, 27, 36);
            ClientSize = new Size(520, 154);
            DoubleBuffered = true;
            FormBorderStyle = Forms.FormBorderStyle.None;
            Opacity = 0;
            ShowInTaskbar = false;
            StartPosition = Forms.FormStartPosition.Manual;
            TopMost = true;

            var accentBar = new Forms.Panel
            {
                BackColor = accent,
                Dock = Forms.DockStyle.Left,
                Width = 6
            };
            var content = new Forms.TableLayoutPanel
            {
                BackColor = Color.Transparent,
                ColumnCount = 1,
                Dock = Forms.DockStyle.Fill,
                Padding = new Forms.Padding(24, 14, 20, 14),
                RowCount = 3
            };
            content.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
            content.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 24));
            content.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 38));
            content.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
            var sourceLabel = new Forms.Label
            {
                AutoEllipsis = false,
                AutoSize = false,
                Dock = Forms.DockStyle.Fill,
                ForeColor = accent,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                Margin = Forms.Padding.Empty,
                Text = "TOOLS FOR STEAM PERFORMANCE"
            };
            var titleLabel = new Forms.Label
            {
                AutoEllipsis = false,
                AutoSize = false,
                Dock = Forms.DockStyle.Fill,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
                Margin = Forms.Padding.Empty,
                Text = title
            };
            var detailLabel = new Forms.Label
            {
                AutoEllipsis = false,
                AutoSize = false,
                Dock = Forms.DockStyle.Fill,
                ForeColor = Color.FromArgb(198, 207, 218),
                Font = new Font("Segoe UI", 10f, FontStyle.Regular),
                Margin = Forms.Padding.Empty,
                Text = detail
            };
            content.Controls.Add(sourceLabel, 0, 0);
            content.Controls.Add(titleLabel, 0, 1);
            content.Controls.Add(detailLabel, 0, 2);
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

            _animationTimer = new Forms.Timer { Interval = 25 };
            _animationTimer.Tick += (_, _) => Animate();
            Shown += (_, _) =>
            {
                TopMost = true;
                _lifetime.Restart();
                _animationTimer.Start();
            };
            FormClosed += (_, _) => _animationTimer.Dispose();
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
            if (IsHandleCreated)
            {
                ApplyRoundedRegion();
            }
        }

        private void ApplyRoundedRegion()
        {
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            const int radius = 18;
            path.AddArc(0, 0, radius, radius, 180, 90);
            path.AddArc(Width - radius, 0, radius, radius, 270, 90);
            path.AddArc(Width - radius, Height - radius, radius, radius, 0, 90);
            path.AddArc(0, Height - radius, radius, radius, 90, 90);
            path.CloseFigure();
            var previousRegion = Region;
            Region = new Region(path);
            previousRegion?.Dispose();
        }

        private void Animate()
        {
            var elapsed = _lifetime.Elapsed.TotalMilliseconds;
            if (elapsed < 250)
            {
                Opacity = Math.Min(1, elapsed / 250);
                return;
            }

            if (elapsed < 4750)
            {
                Opacity = 1;
                return;
            }

            if (elapsed < 5100)
            {
                Opacity = Math.Max(0, 1 - ((elapsed - 4750) / 350));
                return;
            }

            _animationTimer.Stop();
            Close();
        }
    }
}
