using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexQuota
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                if (args.Contains("--install")) { PlatformSupport.SetAutoStart(true); WriteAction("installed", true); return 0; }
                if (args.Contains("--uninstall")) { PlatformSupport.SetAutoStart(false); PlatformSupport.SignalShutdown(); WriteAction("uninstalled", true); return 0; }
                if (args.Contains("--stop")) { PlatformSupport.SignalShutdown(); return 0; }
                if (args.Contains("--diagnose")) return Diagnose();
                if (args.Contains("--render-preview")) return RenderPreview();
                bool created;
                using (var singleInstance = new Mutex(true, PlatformSupport.MutexName, out created))
                {
                    if (!created) return 0;
                    try { return RunApplication(args.Contains("--ui-smoke")); }
                    finally { singleInstance.ReleaseMutex(); }
                }
            }
            catch (Exception error)
            {
                try { WriteAction("failed", false, error.GetType().Name); } catch { }
                return 1;
            }
        }

        static int RunApplication(bool smoke)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var client = new QuotaClient();
            var window = new OverlayWindow(client, true);
            app.MainWindow = window;
            bool newEvent;
            using (var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, PlatformSupport.ShutdownEventName, out newEvent))
            {
                stopEvent.Reset();
                var registration = ThreadPool.RegisterWaitForSingleObject(stopEvent, delegate(object state, bool timedOut) {
                    if (!app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(new Action(delegate { app.Shutdown(); }));
                }, null, Timeout.Infinite, true);
                DispatcherTimer smokeTimer = null;
                if (smoke)
                {
                    smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                    smokeTimer.Tick += delegate { smokeTimer.Stop(); app.Shutdown(); };
                    smokeTimer.Start();
                }
                app.Exit += delegate { if (smokeTimer != null) smokeTimer.Stop(); window.DisposeResources(); registration.Unregister(null); };
                try { return app.Run(); }
                finally { window.DisposeResources(); registration.Unregister(null); }
            }
        }

        static int Diagnose()
        {
            QuotaSnapshot value;
            using (var client = new QuotaClient()) value = client.GetSnapshotAsync().GetAwaiter().GetResult();
            var windows = NativeWindows.GetCodexWindows();
            CodexWindowInfo foreground;
            bool isForeground = NativeWindows.TryGetForegroundCodexWindow(out foreground);
            var report = SnapshotReport(value);
            report["checkedAt"] = DateTimeOffset.UtcNow.ToString("o");
            report["processSessionId"] = Process.GetCurrentProcess().SessionId;
            report["codexVisibleWindowCount"] = windows.Count;
            report["codexForeground"] = isForeground;
            report["windows"] = windows.Select(w => new {
                processId = w.ProcessId, handle = "0x" + w.Handle.ToInt64().ToString("X"),
                left = w.Left, top = w.Top, right = w.Right, bottom = w.Bottom, dpi = NativeWindows.GetDpiForWindow(w.Handle)
            }).ToArray();
            report["autoStartEnabled"] = PlatformSupport.IsAutoStartEnabled();
            WriteJson(Path.Combine(PlatformSupport.StateDirectory, "diagnostics.json"), report);
            return value.Status == QuotaStatus.Ready && windows.Count > 0 ? 0 : 2;
        }

        internal static Dictionary<string, object> SnapshotReport(QuotaSnapshot value)
        {
            return new Dictionary<string, object> {
                { "quotaStatus", value == null ? "Loading" : value.Status.ToString() },
                { "lastUpdatedAt", value != null && value.LastUpdatedAt.HasValue ? value.LastUpdatedAt.Value.ToString("o") : null },
                { "authType", value == null ? null : value.AuthType },
                { "accountFingerprint", value == null ? null : value.AccountFingerprint },
                { "fiveHourRemaining", value == null || value.FiveHour == null ? (double?)null : value.FiveHour.RemainingPercent },
                { "weeklyRemaining", value == null || value.Weekly == null ? (double?)null : value.Weekly.RemainingPercent }
            };
        }

        internal static void WriteJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var serializer = new JavaScriptSerializer();
            File.WriteAllText(path, serializer.Serialize(value), new System.Text.UTF8Encoding(false));
        }

        static void WriteAction(string action, bool success, string errorType = null)
        {
            WriteJson(Path.Combine(PlatformSupport.StateDirectory, "action.json"), new {
                action = action, success = success, errorType = errorType,
                checkedAt = DateTimeOffset.UtcNow.ToString("o")
            });
        }

        static int RenderPreview()
        {
            QuotaSnapshot snapshot;
            using (var client = new QuotaClient()) snapshot = client.GetSnapshotAsync().GetAwaiter().GetResult();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new OverlayWindow(null, false);
            window.DisplaySnapshot(snapshot);
            var element = (FrameworkElement)window.Content;
            element.Measure(new Size(OverlayWindow.BarWidth, OverlayWindow.BarHeight));
            element.Arrange(new Rect(0, 0, OverlayWindow.BarWidth, OverlayWindow.BarHeight));
            element.UpdateLayout();
            var image = new RenderTargetBitmap((int)OverlayWindow.BarWidth * 2, (int)OverlayWindow.BarHeight * 2, 192, 192, PixelFormats.Pbgra32);
            image.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            var folder = Path.Combine(PlatformSupport.ApplicationRoot, "validation");
            Directory.CreateDirectory(folder);
            using (var stream = File.Create(Path.Combine(folder, "usage-bar.png"))) encoder.Save(stream);
            window.DisposeResources(); app.Shutdown();
            return snapshot.Status == QuotaStatus.Ready ? 0 : 2;
        }
    }
}
