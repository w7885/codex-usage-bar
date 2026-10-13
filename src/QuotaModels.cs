using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexQuota
{
    public enum QuotaStatus { Loading, Ready, Error, SignedOut, UnsupportedAccount }

    public sealed class QuotaWindow
    {
        public double UsedPercent { get; internal set; }
        public double RemainingPercent { get; internal set; }
        public int WindowDurationMinutes { get; internal set; }
        public long? ResetsAt { get; internal set; }
        public DateTimeOffset? ResetTime
        {
            get
            {
                if (!ResetsAt.HasValue) return null;
                try { return new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(ResetsAt.Value); }
                catch (ArgumentOutOfRangeException) { return null; }
            }
        }
    }

    public sealed class QuotaSnapshot
    {
        public QuotaWindow FiveHour { get; internal set; }
        public QuotaWindow Weekly { get; internal set; }
        public DateTimeOffset? LastUpdatedAt { get; internal set; }
        public QuotaStatus Status { get; internal set; }
        public string ErrorMessage { get; internal set; }
        public string AuthType { get; internal set; }
        public string AccountFingerprint { get; internal set; }
        public long Revision { get; internal set; }
        public bool HasData { get { return FiveHour != null || Weekly != null; } }

        internal QuotaSnapshot WithStatus(QuotaStatus status, string error)
        {
            return new QuotaSnapshot
            {
                FiveHour = FiveHour, Weekly = Weekly, LastUpdatedAt = LastUpdatedAt,
                Status = status, ErrorMessage = error, AuthType = AuthType,
                AccountFingerprint = AccountFingerprint
            };
        }
    }

    internal static class QuotaParser
    {
        internal static IDictionary<string, object> JsonObject(string json)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
            return AsObject(serializer.DeserializeObject(json));
        }

        internal static IDictionary<string, object> AsObject(object value)
        {
            return value as IDictionary<string, object>;
        }

        internal static object Get(IDictionary<string, object> value, string key)
        {
            object item;
            return value != null && value.TryGetValue(key, out item) ? item : null;
        }

        internal static string Text(object value)
        {
            return value as string;
        }

        internal static string AuthType(IDictionary<string, object> account)
        {
            var type = Text(Get(account, "type"));
            if (account == null) return "signedOut";
            if (type == "chatgpt" || type == "chatgptAuthTokens" || type == "apiKey" || type == "amazonBedrock") return type;
            return "unsupported";
        }

        internal static string Fingerprint(IDictionary<string, object> account)
        {
            foreach (var field in new[] { "chatgptAccountId", "accountId", "id", "email" })
            {
                var identity = Text(Get(account, field));
                if (String.IsNullOrWhiteSpace(identity)) continue;
                using (var hash = SHA256.Create())
                {
                    var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(identity));
                    return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant().Substring(0, 12);
                }
            }
            return null;
        }

        internal static QuotaSnapshot ParseLimits(IDictionary<string, object> result, string authType, string fingerprint, DateTimeOffset now)
        {
            var byLimit = AsObject(Get(result, "rateLimitsByLimitId"));
            var bucket = AsObject(Get(byLimit, "codex"));
            if (bucket == null)
            {
                var legacy = AsObject(Get(result, "rateLimits"));
                var id = Text(Get(legacy, "limitId"));
                if (id == "codex" || (byLimit == null && String.IsNullOrEmpty(id))) bucket = legacy;
            }
            var snapshot = new QuotaSnapshot
            {
                Status = QuotaStatus.Ready, AuthType = authType,
                AccountFingerprint = fingerprint, LastUpdatedAt = now
            };
            if (bucket == null) return snapshot;
            foreach (var name in new[] { "primary", "secondary" })
            {
                var window = ParseWindow(AsObject(Get(bucket, name)));
                if (window == null) continue;
                if (window.WindowDurationMinutes == 300 && snapshot.FiveHour == null) snapshot.FiveHour = window;
                if (window.WindowDurationMinutes == 10080 && snapshot.Weekly == null) snapshot.Weekly = window;
            }
            return snapshot;
        }

        private static QuotaWindow ParseWindow(IDictionary<string, object> data)
        {
            double used, duration;
            if (!Number(Get(data, "usedPercent"), out used) || !Number(Get(data, "windowDurationMins"), out duration)) return null;
            if (duration != 300 && duration != 10080) return null;
            long? reset = null;
            double resetValue;
            if (Number(Get(data, "resetsAt"), out resetValue) && resetValue >= 0 && resetValue < 253402300800 && resetValue == Math.Truncate(resetValue)) reset = (long)resetValue;
            return new QuotaWindow
            {
                UsedPercent = used, RemainingPercent = Math.Max(0, Math.Min(100, 100 - used)),
                WindowDurationMinutes = (int)duration, ResetsAt = reset
            };
        }

        private static bool Number(object value, out double number)
        {
            number = 0;
            if (!(value is int || value is long || value is decimal || value is double || value is float)) return false;
            try { number = Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch (Exception) { return false; }
            return !Double.IsNaN(number) && !Double.IsInfinity(number);
        }
    }
}
