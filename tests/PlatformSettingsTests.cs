using System;
using System.IO;
using System.Reflection;

namespace CodexQuota.Tests
{
    internal static class PlatformSettingsTests
    {
        internal static void Run()
        {
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string testRoot = Path.Combine(temporaryRoot, "CodexUsageBarPlatformTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);
            AppDomain domain = null;
            try
            {
                domain = AppDomain.CreateDomain("IsolatedPlatformSettings", null, new AppDomainSetup { ApplicationBase = testRoot });
                var isolated = (IsolatedPlatformSettings)domain.CreateInstanceFromAndUnwrap(
                    Assembly.GetExecutingAssembly().Location, typeof(IsolatedPlatformSettings).FullName);
                int checks = isolated.RunChecks();
                Console.WriteLine("PASS platform settings: " + checks + " checks in isolated temporary state");
            }
            finally
            {
                if (domain != null) AppDomain.Unload(domain);
                string resolved = Path.GetFullPath(testRoot);
                if (resolved.StartsWith(temporaryRoot + "CodexUsageBarPlatformTests_", StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(resolved, true);
            }
        }
    }

    public sealed class IsolatedPlatformSettings : MarshalByRefObject
    {
        int count;
        void Check(bool valid, string label) { if (!valid) throw new InvalidOperationException(label); count++; }
        public int RunChecks()
        {
            Check(NativeWindows.IsCodexExecutablePath(@"C:\Program Files\WindowsApps\OpenAI.Codex_1.0_x64\app\ChatGPT.exe"), "Codex identity");
            Check(!NativeWindows.IsCodexExecutablePath(@"C:\Program Files\WindowsApps\OpenAI.ChatGPT_1.0_x64\app\ChatGPT.exe"), "Ordinary ChatGPT rejected");
            Check(!NativeWindows.IsCodexExecutablePath(@"C:\example\codex.exe"), "CLI rejected");
            Check(!NativeWindows.IsCodexExecutablePath(null), "Missing path rejected");
            var defaults = PlatformSupport.LoadSettings();
            Check(defaults.OffsetX == 0 && defaults.OffsetY == 8, "Default position");
            PlatformSupport.SaveSettings(new UsageBarSettings { OffsetX = -42.5, OffsetY = 12.25 });
            var loaded = PlatformSupport.LoadSettings();
            Check(loaded.OffsetX == -42.5 && loaded.OffsetY == 12.25, "Position round trip");
            string saved = File.ReadAllText(PlatformSupport.SettingsPath);
            bool rejected = false;
            try { PlatformSupport.SaveSettings(new UsageBarSettings { OffsetX = Double.NaN, OffsetY = 8 }); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && File.ReadAllText(PlatformSupport.SettingsPath) == saved, "Invalid position does not overwrite");
            File.WriteAllText(PlatformSupport.SettingsPath, "{broken-json");
            defaults = PlatformSupport.LoadSettings();
            Check(defaults.OffsetX == 0 && defaults.OffsetY == 8, "Damaged settings recover");
            File.WriteAllText(PlatformSupport.SettingsPath, "{\"offsetX\":-2.5}");
            loaded = PlatformSupport.LoadSettings();
            Check(loaded.OffsetX == -2.5 && loaded.OffsetY == 8, "Missing member defaults");
            Check(PlatformSupport.MutexName.StartsWith(@"Local\CodexUsageBar_"), "User-scoped mutex");
            Check(PlatformSupport.ShutdownEventName.StartsWith(@"Local\CodexUsageBar_Stop_"), "User-scoped stop event");
            return count;
        }
    }
}
