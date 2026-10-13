using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace CodexQuota
{
    [DataContract]
    public sealed class UsageBarSettings
    {
        public UsageBarSettings() { OffsetY = 8; }
        [DataMember(Name = "offsetX")] public double OffsetX { get; set; }
        [DataMember(Name = "offsetY")] public double OffsetY { get; set; }
        [OnDeserializing] private void InitializeDefaults(StreamingContext context) { OffsetY = 8; }
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
                    if (settings != null && ValidOffset(settings.OffsetX) && ValidOffset(settings.OffsetY)) return settings;
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
            if (!ValidOffset(settings.OffsetX) || !ValidOffset(settings.OffsetY)) throw new ArgumentOutOfRangeException("settings");
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
