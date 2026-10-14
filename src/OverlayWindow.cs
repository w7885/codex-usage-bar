using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexQuota
{
    public struct PixelBounds
    {
        public int Left, Top, Width, Height;
    }

    public static class OverlayPlacement
    {
        public static PixelBounds Calculate(int left, int top, int right, int bottom,
            double dpi, double widthDip, double heightDip, double offsetX, double offsetY)
        {
            double scale = dpi > 0 && !Double.IsNaN(dpi) ? dpi / 96.0 : 1.0;
            int width = Math.Min(Math.Max(1, right - left), Math.Max(1, (int)Math.Round(widthDip * scale)));
            int height = Math.Min(Math.Max(1, bottom - top), Math.Max(1, (int)Math.Round(heightDip * scale)));
            int x = left + (right - left - width) / 2 + (int)Math.Round(offsetX * scale);
            int y = top + (int)Math.Round(offsetY * scale);
            return new PixelBounds {
                Left = Math.Max(left, Math.Min(right - width, x)),
                Top = Math.Max(top, Math.Min(bottom - height, y)),
                Width = width, Height = height
            };
        }
    }

    public sealed class OverlayWindow : Window
    {
        public const double BarWidth = 376;
        public const double BarHeight = 40;
        readonly QuotaClient client;
        readonly UsageBarSettings settings;
        readonly DispatcherTimer followTimer;
        readonly DispatcherTimer refreshTimer;
        readonly TextBlock fiveText, weekText, fiveLabel, weekLabel, statusText, dragHandle;
        readonly Border fiveFill, weekFill, fiveTrack, weekTrack;
        readonly Border panel;
        readonly System.Windows.Forms.NotifyIcon tray;
        readonly System.Windows.Forms.ToolStripMenuItem autoStart;
        CodexWindowInfo target;
        QuotaSnapshot snapshot;
        bool foreground, refreshing, disposed, dragging;
        Point dragStart;
        double startOffsetX, startOffsetY;
        DateTimeOffset lastRequest = DateTimeOffset.MinValue;
        DateTimeOffset lastStatusWrite = DateTimeOffset.MinValue;
        readonly bool diagnosticsEnabled;

        [StructLayout(LayoutKind.Sequential)] struct CursorPoint { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out CursorPoint point);

        public OverlayWindow(QuotaClient quotaClient, bool enableTimers)
        {
            client = quotaClient;
            diagnosticsEnabled = enableTimers;
            settings = PlatformSupport.LoadSettings();
            Title = "Codex 剩余用量";
            Width = BarWidth; Height = BarHeight;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            UseLayoutRounding = true;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            FontSize = 12;

            panel = new Border {
                Background = ColorBrush("#202123"), BorderBrush = ColorBrush("#48494C"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 0, 10, 0)
            };
            ToolTipService.SetInitialShowDelay(panel, 250);
            ToolTipService.SetShowDuration(panel, 60000);
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            dragHandle = new TextBlock { Text = "⋮⋮", FontSize = 15, Foreground = ColorBrush("#9EA0A5"),
                Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(dragHandle);
            var fiveGroup = CreateQuotaGroup("5 小时", out fiveLabel, out fiveText, out fiveFill, out fiveTrack);
            var weekGroup = CreateQuotaGroup("一周", out weekLabel, out weekText, out weekFill, out weekTrack);
            row.Children.Add(fiveGroup);
            row.Children.Add(new Border { Width = 1, Height = 18, Background = ColorBrush("#4A4B4E"), Margin = new Thickness(10, 0, 10, 0) });
            row.Children.Add(weekGroup);
            statusText = new TextBlock { Text = "读取中", FontSize = 11, Foreground = ColorBrush("#ACAFB4"),
                Width = 62, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            row.Children.Add(statusText);
            panel.Child = row;
            Content = panel;
            AutomationProperties.SetName(panel, "Codex 5 小时和一周剩余用量");
            SourceInitialized += delegate { NativeWindows.AttachNoActivate(this); };
            panel.MouseLeftButtonDown += BeginDrag;
            panel.MouseMove += ContinueDrag;
            panel.MouseLeftButtonUp += EndDrag;
            panel.LostMouseCapture += delegate { dragging = false; };
            panel.MouseRightButtonUp += delegate { if (tray != null) tray.ContextMenuStrip.Show(System.Windows.Forms.Cursor.Position); };

            followTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
            followTimer.Tick += delegate { FollowForegroundWindow(); };
            refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(60) };
            refreshTimer.Tick += delegate { if (foreground) RefreshNow(); };

            if (enableTimers)
            {
                tray = new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information,
                    Text = "Codex 剩余用量", Visible = true };
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("立即刷新", null, delegate { Dispatcher.BeginInvoke(new Action(RefreshNow)); });
                menu.Items.Add("用量详情", null, delegate { Dispatcher.BeginInvoke(new Action(ShowDetails)); });
                menu.Items.Add("自定义配色…", null, delegate { Dispatcher.BeginInvoke(new Action(ShowColorSettings)); });
                menu.Items.Add("恢复默认位置", null, delegate { Dispatcher.BeginInvoke(new Action(ResetPosition)); });
                autoStart = new System.Windows.Forms.ToolStripMenuItem("登录 Windows 后自动启动") { Checked = PlatformSupport.IsAutoStartEnabled(), CheckOnClick = false };
                autoStart.Click += delegate {
                    try { PlatformSupport.SetAutoStart(!PlatformSupport.IsAutoStartEnabled()); autoStart.Checked = PlatformSupport.IsAutoStartEnabled(); }
                    catch { ShowTrayMessage("无法修改自启动设置，请通过安装或移除入口重试。"); }
                };
                menu.Items.Add(autoStart);
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add("退出", null, delegate { Dispatcher.BeginInvoke(new Action(delegate { Application.Current.Shutdown(); })); });
                tray.ContextMenuStrip = menu;
                tray.DoubleClick += delegate { Dispatcher.BeginInvoke(new Action(ShowDetails)); };
                if (client != null) client.SnapshotUpdated += ReceiveSnapshot;
                followTimer.Start(); refreshTimer.Start();
                Dispatcher.BeginInvoke(new Action(FollowForegroundWindow));
            }
            ApplyColorSettings();
            Closed += delegate { DisposeResources(); };
        }

        static Brush ColorBrush(string color)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color);
            brush.Freeze(); return brush;
        }

        static StackPanel CreateQuotaGroup(string name, out TextBlock label, out TextBlock value, out Border fill, out Border track)
        {
            var group = new StackPanel { Width = 118, Orientation = Orientation.Vertical };
            var labels = new Grid();
            label = new TextBlock { Text = name, Foreground = ColorBrush("#CFD1D5") };
            labels.Children.Add(label);
            value = new TextBlock { Text = "—", Foreground = ColorBrush("#F5F6F7"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
            labels.Children.Add(value);
            group.Children.Add(labels);
            fill = new Border { Width = 0, Height = 3, Background = ColorBrush("#88D6B5"), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
            track = new Border { Height = 3, Width = 118, Background = ColorBrush("#4B4D52"), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 4, 0, 0), Child = fill };
            group.Children.Add(track);
            return group;
        }

        void ReceiveSnapshot(QuotaSnapshot value)
        {
            if (!disposed && !Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(delegate { if (!disposed) DisplaySnapshot(value); }));
        }

        public void DisplaySnapshot(QuotaSnapshot value)
        {
            if (snapshot != null && value != null && value.Revision < snapshot.Revision) return;
            snapshot = value;
            UpdateWindow(value == null ? null : value.FiveHour, fiveText, fiveFill);
            UpdateWindow(value == null ? null : value.Weekly, weekText, weekFill);
            string status = value == null ? "读取中" : StatusLabel(value);
            statusText.Text = status;
            statusText.Foreground = ColorBrush(value != null && value.Status == QuotaStatus.Error
                ? UsageBarPalette.ErrorColor(settings.BackgroundColor) : UsageBarPalette.MutedTextColor(settings.BackgroundColor));
            panel.ToolTip = DetailsText();
            AutomationProperties.SetHelpText(panel, DetailsText());
            if (tray != null)
            {
                string caption = "Codex 剩余 · 5小时 " + fiveText.Text + " · 一周 " + weekText.Text;
                tray.Text = caption.Length > 63 ? caption.Substring(0, 63) : caption;
            }
            WriteRuntimeStatus(false);
        }

        void UpdateWindow(QuotaWindow value, TextBlock text, Border fill)
        {
            if (value == null) { text.Text = "暂无数据"; fill.Width = 0; fill.Background = ColorBrush(settings.ProgressColor); return; }
            text.Text = value.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
            fill.Width = 118 * value.RemainingPercent / 100.0;
            fill.Background = ColorBrush(value.RemainingPercent <= 10 ? "#FF9990" : value.RemainingPercent <= 20 ? "#F3C779" : settings.ProgressColor);
        }

        void ApplyColorSettings()
        {
            panel.Background = ColorBrush(settings.BackgroundColor);
            panel.BorderBrush = ColorBrush(UsageBarPalette.BorderColor(settings.BackgroundColor));
            var secondaryColor = ColorBrush(UsageBarPalette.SecondaryTextColor(settings.BackgroundColor));
            dragHandle.Foreground = secondaryColor;
            fiveLabel.Foreground = secondaryColor;
            weekLabel.Foreground = secondaryColor;
            statusText.Foreground = ColorBrush(UsageBarPalette.MutedTextColor(settings.BackgroundColor));
            fiveText.Foreground = ColorBrush(settings.TextColor);
            weekText.Foreground = ColorBrush(settings.TextColor);
            var trackColor = ColorBrush(UsageBarPalette.TrackColor(settings.BackgroundColor));
            fiveTrack.Background = trackColor;
            weekTrack.Background = trackColor;
            if (snapshot == null)
            {
                fiveFill.Background = ColorBrush(settings.ProgressColor);
                weekFill.Background = ColorBrush(settings.ProgressColor);
            }
            else
            {
                UpdateWindow(snapshot.FiveHour, fiveText, fiveFill);
                UpdateWindow(snapshot.Weekly, weekText, weekFill);
            }
        }

        static string StatusLabel(QuotaSnapshot value)
        {
            if (value.Status == QuotaStatus.Error) return "更新失败";
            if (value.Status == QuotaStatus.SignedOut) return "需登录";
            if (value.Status == QuotaStatus.UnsupportedAccount) return "账号不支持";
            if (value.Status == QuotaStatus.Loading) return "读取中";
            if (value.FiveHour == null || value.Weekly == null) return "暂无数据";
            return "剩余用量";
        }

        string DetailsText()
        {
            string text = "Codex 剩余用量\n5 小时：" + fiveText.Text + ResetText(snapshot == null ? null : snapshot.FiveHour)
                + "\n一周：" + weekText.Text + ResetText(snapshot == null ? null : snapshot.Weekly);
            text += "\n上次成功更新：" + (snapshot != null && snapshot.LastUpdatedAt.HasValue ? snapshot.LastUpdatedAt.Value.ToLocalTime().ToString("MM/dd HH:mm:ss") : "尚未成功读取");
            if (snapshot != null && snapshot.Status != QuotaStatus.Ready)
                text += "\n状态：" + StatusLabel(snapshot) + "\n" + FriendlyError(snapshot);
            text += "\n拖动调整位置；双击恢复默认；右键打开菜单。";
            return text;
        }

        static string ResetText(QuotaWindow value)
        {
            return value != null && value.ResetTime.HasValue ? " · 恢复于 " + value.ResetTime.Value.ToLocalTime().ToString("MM/dd HH:mm") : "";
        }

        static string FriendlyError(QuotaSnapshot value)
        {
            if (value.Status == QuotaStatus.SignedOut) return "请在 Codex 中登录后刷新。";
            if (value.Status == QuotaStatus.UnsupportedAccount) return "当前登录方式不提供订阅剩余用量。";
            if (value.ErrorMessage == "quota_timeout") return "读取超时，请稍后刷新。";
            if (value.ErrorMessage == "quota_disconnected") return "连接已断开，请稍后刷新。";
            if (value.ErrorMessage == "codex_not_found") return "未找到 Codex 运行组件，请重新打开 Codex 后重试。";
            if (value.Status == QuotaStatus.Loading) return "正在读取当前账号的用量。";
            return "暂时无法读取用量，请确认 Codex 登录正常后刷新。";
        }

        void FollowForegroundWindow()
        {
            if (disposed) return;
            CodexWindowInfo current;
            bool found = NativeWindows.TryGetForegroundCodexWindow(out current);
            bool resumed = found && !foreground;
            foreground = found;
            if (!found) { if (IsVisible && !dragging) Hide(); WriteRuntimeStatus(false); return; }
            target = current;
            if (!IsVisible) Show();
            if (!dragging) Place();
            if (resumed && (DateTimeOffset.UtcNow - lastRequest).TotalSeconds >= 5) RefreshNow();
            WriteRuntimeStatus(false);
        }

        void Place()
        {
            if (target == null) return;
            var bounds = OverlayPlacement.Calculate(target.Left, target.Top, target.Right, target.Bottom,
                NativeWindows.GetDpiForWindow(target.Handle), BarWidth, BarHeight, settings.OffsetX, settings.OffsetY);
            NativeWindows.SetOverlayBounds(this, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }

        public async void RefreshNow()
        {
            if (disposed || refreshing || client == null) return;
            refreshing = true; lastRequest = DateTimeOffset.UtcNow;
            statusText.Text = "更新中";
            try { await client.GetSnapshotAsync(); }
            catch { if (!disposed) { statusText.Text = "更新失败"; panel.ToolTip = DetailsText() + "\n暂时无法读取，请稍后刷新。"; } }
            finally { refreshing = false; }
        }

        void BeginDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { ResetPosition(); e.Handled = true; return; }
            if (target == null) return;
            CursorPoint cursor;
            if (!GetCursorPos(out cursor)) return;
            dragStart = new Point(cursor.X, cursor.Y);
            startOffsetX = settings.OffsetX; startOffsetY = settings.OffsetY;
            dragging = true; panel.CaptureMouse(); e.Handled = true;
        }

        void ContinueDrag(object sender, MouseEventArgs e)
        {
            if (!dragging || target == null) return;
            CursorPoint cursor;
            if (!GetCursorPos(out cursor)) return;
            double scale = NativeWindows.GetDpiForWindow(target.Handle) / 96.0;
            if (scale <= 0) scale = 1;
            settings.OffsetX = startOffsetX + (cursor.X - dragStart.X) / scale;
            settings.OffsetY = startOffsetY + (cursor.Y - dragStart.Y) / scale;
            Place();
        }

        void EndDrag(object sender, MouseButtonEventArgs e)
        {
            if (!dragging) return;
            dragging = false; panel.ReleaseMouseCapture();
            try { PlatformSupport.SaveSettings(settings); }
            catch { ShowTrayMessage("位置暂未保存；本次运行仍使用新位置。"); }
            if (!foreground) Hide();
            e.Handled = true;
        }

        void ResetPosition()
        {
            settings.OffsetX = 0; settings.OffsetY = 8;
            try { PlatformSupport.SaveSettings(settings); } catch { ShowTrayMessage("默认位置暂未保存。"); }
            Place();
        }

        void ShowColorSettings()
        {
            var dialog = new ColorSettingsWindow(settings) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            string oldBackground = settings.BackgroundColor;
            string oldText = settings.TextColor;
            string oldProgress = settings.ProgressColor;
            settings.BackgroundColor = dialog.SelectedBackgroundColor;
            settings.TextColor = dialog.SelectedTextColor;
            settings.ProgressColor = dialog.SelectedProgressColor;
            try { PlatformSupport.SaveSettings(settings); }
            catch
            {
                settings.BackgroundColor = oldBackground;
                settings.TextColor = oldText;
                settings.ProgressColor = oldProgress;
                ShowTrayMessage("配色暂未保存；本次运行仍使用原配色。");
                return;
            }
            ApplyColorSettings();
        }

        void ShowDetails() { MessageBox.Show(DetailsText(), "Codex 剩余用量", MessageBoxButton.OK, MessageBoxImage.Information); }
        void ShowTrayMessage(string text)
        {
            if (tray != null) { tray.BalloonTipTitle = "Codex 剩余用量"; tray.BalloonTipText = text; tray.ShowBalloonTip(4000); }
        }

        void WriteRuntimeStatus(bool stopped)
        {
            if (!diagnosticsEnabled || (!stopped && (DateTimeOffset.UtcNow - lastStatusWrite).TotalSeconds < 2)) return;
            lastStatusWrite = DateTimeOffset.UtcNow;
            try
            {
                var report = Program.SnapshotReport(snapshot);
                report["running"] = !stopped;
                report["overlayVisible"] = !stopped && IsVisible;
                report["codexForeground"] = !stopped && foreground;
                report["targetProcessId"] = target == null ? (int?)null : target.ProcessId;
                report["targetHandle"] = target == null ? null : "0x" + target.Handle.ToInt64().ToString("X");
                report["foregroundHandle"] = "0x" + NativeWindows.GetForegroundTopLevelWindow().ToInt64().ToString("X");
                report["overlayHandle"] = "0x" + new WindowInteropHelper(this).Handle.ToInt64().ToString("X");
                report["processId"] = System.Diagnostics.Process.GetCurrentProcess().Id;
                report["checkedAt"] = lastStatusWrite.ToString("o");
                Program.WriteJson(System.IO.Path.Combine(PlatformSupport.StateDirectory, "runtime-status.json"), report);
            }
            catch { }
        }

        public void DisposeResources()
        {
            if (disposed) return;
            disposed = true; followTimer.Stop(); refreshTimer.Stop();
            if (client != null) { client.SnapshotUpdated -= ReceiveSnapshot; client.Dispose(); }
            if (tray != null) { tray.Visible = false; if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose(); tray.Dispose(); }
            WriteRuntimeStatus(true);
        }
    }
}
