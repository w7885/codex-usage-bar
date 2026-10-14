using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;
using System.Windows.Media;

namespace CodexQuota
{
    [DataContract]
    public sealed class UsageBarSettings
    {
        public const string DefaultBackgroundColor = "#202123";
        public const string DefaultTextColor = "#F5F6F7";
        public const string DefaultProgressColor = "#88D6B5";

        public UsageBarSettings() { SetDefaultValues(); }
        [DataMember(Name = "offsetX")] public double OffsetX { get; set; }
        [DataMember(Name = "offsetY")] public double OffsetY { get; set; }
        [DataMember(Name = "backgroundColor")] public string BackgroundColor { get; set; }
        [DataMember(Name = "textColor")] public string TextColor { get; set; }
        [DataMember(Name = "progressColor")] public string ProgressColor { get; set; }

        [OnDeserializing] private void InitializeDefaults(StreamingContext context) { SetDefaultValues(); }

        public void RestoreDefaultColors()
        {
            BackgroundColor = DefaultBackgroundColor;
            TextColor = DefaultTextColor;
            ProgressColor = DefaultProgressColor;
        }

        private void SetDefaultValues()
        {
            OffsetY = 8;
            RestoreDefaultColors();
        }
    }

    internal static class UsageBarPalette
    {
        internal static string BorderColor(string background)
        {
            if (String.Equals(background, UsageBarSettings.DefaultBackgroundColor, StringComparison.OrdinalIgnoreCase)) return "#48494C";
            return Blend(background, SecondaryTextColor(background), 0.28);
        }

        internal static string TrackColor(string background)
        {
            if (String.Equals(background, UsageBarSettings.DefaultBackgroundColor, StringComparison.OrdinalIgnoreCase)) return "#4B4D52";
            return Blend(background, SecondaryTextColor(background), 0.18);
        }

        internal static string SecondaryTextColor(string background)
        {
            if (String.Equals(background, UsageBarSettings.DefaultBackgroundColor, StringComparison.OrdinalIgnoreCase)) return "#CFD1D5";
            return Luminance(Parse(background)) >= 0.18 ? "#202124" : "#F5F6F7";
        }

        internal static string MutedTextColor(string background)
        {
            if (String.Equals(background, UsageBarSettings.DefaultBackgroundColor, StringComparison.OrdinalIgnoreCase)) return "#ACAFB4";
            return SecondaryTextColor(background);
        }

        internal static string ErrorColor(string background)
        {
            if (String.Equals(background, UsageBarSettings.DefaultBackgroundColor, StringComparison.OrdinalIgnoreCase)) return "#F3C779";
            return Luminance(Parse(background)) >= 0.18 ? "#805000" : "#F3C779";
        }

        private static string Blend(string first, string second, double amount)
        {
            Color a = Parse(first), b = Parse(second);
            return "#" + Mix(a.R, b.R, amount).ToString("X2")
                + Mix(a.G, b.G, amount).ToString("X2")
                + Mix(a.B, b.B, amount).ToString("X2");
        }

        private static byte Mix(byte first, byte second, double amount)
        {
            return (byte)Math.Round(first + (second - first) * amount);
        }

        private static Color Parse(string value)
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }

        private static double Luminance(Color color)
        {
            return 0.2126 * Linear(color.R / 255.0) + 0.7152 * Linear(color.G / 255.0) + 0.0722 * Linear(color.B / 255.0);
        }

        private static double Linear(double channel)
        {
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
    }

    public static class PlatformSupport
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "CodexUsageBar";

        public static string ApplicationRoot
        {
            get
            {
                string baseDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
                if (String.Equals(Path.GetFileName(baseDirectory), "dist", StringComparison.OrdinalIgnoreCase))
                    return Directory.GetParent(baseDirectory).FullName;
                return baseDirectory;
            }
        }

        public static string StateDirectory { get { return Path.Combine(ApplicationRoot, "state"); } }
        public static string SettingsPath { get { return Path.Combine(StateDirectory, "settings.json"); } }
        public static string ExecutablePath
        {
            get
            {
                Assembly entry = Assembly.GetEntryAssembly();
                return Path.GetFullPath(entry == null ? Assembly.GetExecutingAssembly().Location : entry.Location);
            }
        }

        private static string UserId
        {
            get
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    return identity.User == null ? Environment.UserName : identity.User.Value;
            }
        }
        public static string MutexName { get { return @"Local\CodexUsageBar_" + UserId; } }
        public static string ShutdownEventName { get { return @"Local\CodexUsageBar_Stop_" + UserId; } }

        public static UsageBarSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new UsageBarSettings();
                using (var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var settings = (UsageBarSettings)new DataContractJsonSerializer(typeof(UsageBarSettings)).ReadObject(stream);
                    if (settings != null && ValidOffset(settings.OffsetX) && ValidOffset(settings.OffsetY))
                    {
                        if (!ValidColor(settings.BackgroundColor)) settings.BackgroundColor = UsageBarSettings.DefaultBackgroundColor;
                        if (!ValidColor(settings.TextColor)) settings.TextColor = UsageBarSettings.DefaultTextColor;
                        if (!ValidColor(settings.ProgressColor)) settings.ProgressColor = UsageBarSettings.DefaultProgressColor;
                        return settings;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SerializationException) { }
            catch (FormatException) { }
            return new UsageBarSettings();
        }

        public static void SaveSettings(UsageBarSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (!ValidOffset(settings.OffsetX) || !ValidOffset(settings.OffsetY)
                || !ValidColor(settings.BackgroundColor) || !ValidColor(settings.TextColor) || !ValidColor(settings.ProgressColor))
                throw new ArgumentOutOfRangeException("settings");
            Directory.CreateDirectory(StateDirectory);
            string temporary = SettingsPath + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(UsageBarSettings)).WriteObject(stream, settings);
                    stream.Flush(true);
                }
                if (File.Exists(SettingsPath)) File.Replace(temporary, SettingsPath, null);
                else File.Move(temporary, SettingsPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static bool ValidOffset(double offset)
        {
            return !Double.IsNaN(offset) && !Double.IsInfinity(offset) && Math.Abs(offset) <= 10000;
        }

        internal static bool ValidColor(string color)
        {
            if (String.IsNullOrEmpty(color) || color.Length != 7 || color[0] != '#') return false;
            for (int index = 1; index < color.Length; index++)
                if (!Uri.IsHexDigit(color[index])) return false;
            return true;
        }

        public static bool IsAutoStartEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
            {
                string command = key == null ? null : key.GetValue(RunValueName) as string;
                return String.Equals(command, "\"" + ExecutablePath + "\"", StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void SetAutoStart(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null) throw new IOException("Cannot open the current user's startup settings.");
                if (enabled) key.SetValue(RunValueName, "\"" + ExecutablePath + "\"", RegistryValueKind.String);
                else key.DeleteValue(RunValueName, false);
            }
        }

        public static bool SignalShutdown()
        {
            try
            {
                using (EventWaitHandle shutdown = EventWaitHandle.OpenExisting(ShutdownEventName)) return shutdown.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
