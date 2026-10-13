using System;
using CodexQuota;

internal static class QuotaTests
{
    internal static void Run()
    {
        MultiBucketWinsAndWindowsCanSwap();
        LegacyAndNullWindowsStayUnknown();
        ClampAndInvalidFields();
        PastResetDoesNotInventUsage();
        ErrorsRetainLastGoodData();
        FingerprintsDoNotExposeIdentity();
        Console.WriteLine("PASS quota parsing and state tests (6 scenarios)");
    }

    private static QuotaSnapshot Parse(string json)
    {
        return QuotaParser.ParseLimits(QuotaParser.JsonObject(json), "chatgpt", "test-fingerprint", new DateTimeOffset(2026, 10, 13, 3, 0, 0, TimeSpan.Zero));
    }

    private static void MultiBucketWinsAndWindowsCanSwap()
    {
        var result = Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":99,\"windowDurationMins\":300}},\"rateLimitsByLimitId\":{\"other\":{\"primary\":{\"usedPercent\":1,\"windowDurationMins\":300}},\"codex\":{\"primary\":{\"usedPercent\":35,\"windowDurationMins\":10080},\"secondary\":{\"usedPercent\":63,\"windowDurationMins\":300}}}}");
        Assert(result.FiveHour.RemainingPercent == 37 && result.Weekly.RemainingPercent == 65, "Choose codex bucket and identify windows by duration.");
        var unrelated = Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":5,\"windowDurationMins\":300}},\"rateLimitsByLimitId\":{\"other\":{\"primary\":{\"usedPercent\":1,\"windowDurationMins\":300}}}}");
        Assert(!unrelated.HasData, "A legacy bucket without identity cannot override a known non-codex map.");
        var namedFallback = Parse("{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":5,\"windowDurationMins\":300}},\"rateLimitsByLimitId\":{}}");
        Assert(namedFallback.FiveHour.RemainingPercent == 95, "Explicit legacy codex bucket is a safe fallback.");
    }

    private static void LegacyAndNullWindowsStayUnknown()
    {
        var legacy = Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":25,\"windowDurationMins\":300},\"secondary\":null}}");
        Assert(legacy.FiveHour.RemainingPercent == 75 && legacy.Weekly == null, "Missing weekly quota remains unknown.");
        var empty = Parse("{\"rateLimits\":null,\"rateLimitsByLimitId\":null}");
        Assert(!empty.HasData && empty.FiveHour == null && empty.Weekly == null, "Null does not become zero or 100 percent.");
        var other = Parse("{\"rateLimits\":{\"limitId\":\"codex_other\",\"primary\":{\"usedPercent\":20,\"windowDurationMins\":300}}}");
        Assert(!other.HasData, "Ignore unrelated legacy bucket.");
    }

    private static void ClampAndInvalidFields()
    {
        var result = Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":-3,\"windowDurationMins\":300,\"resetsAt\":\"secret\"},\"secondary\":{\"usedPercent\":108,\"windowDurationMins\":10080,\"resetsAt\":253402300800}}}");
        Assert(result.FiveHour.RemainingPercent == 100 && result.Weekly.RemainingPercent == 0, "Clamp displayed remaining percentages.");
        Assert(result.FiveHour.ResetTime == null && result.Weekly.ResetTime == null, "Invalid reset timestamps remain unknown.");
        var wrongTypes = Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":\"25\",\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":25,\"windowDurationMins\":60}}}");
        Assert(!wrongTypes.HasData, "Reject invalid values and unsupported quota lengths.");
    }

    private static void PastResetDoesNotInventUsage()
    {
        var result = Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":42,\"windowDurationMins\":300,\"resetsAt\":1}}}");
        Assert(result.FiveHour.RemainingPercent == 58 && result.FiveHour.ResetsAt == 1, "An elapsed reset time does not imply a fresh quota value.");
        Assert(result.LastUpdatedAt.HasValue && result.Status == QuotaStatus.Ready, "Successful full reads carry the observation time.");
    }

    private static void ErrorsRetainLastGoodData()
    {
        var good = Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":63,\"windowDurationMins\":300}}}");
        var failed = good.WithStatus(QuotaStatus.Error, "quota_timeout");
        Assert(failed.FiveHour.RemainingPercent == 37 && failed.LastUpdatedAt == good.LastUpdatedAt, "Errors preserve data and its original observation time.");
        Assert(failed.ErrorMessage == "quota_timeout" && good.Status == QuotaStatus.Ready, "Failure produces a new snapshot without mutating the successful snapshot.");
        bool invalid = false;
        try { QuotaParser.JsonObject("{invalid"); } catch (ArgumentException) { invalid = true; }
        Assert(invalid, "Malformed JSON fails explicitly.");
    }

    private static void FingerprintsDoNotExposeIdentity()
    {
        var first = QuotaParser.JsonObject("{\"type\":\"chatgpt\",\"email\":\"synthetic@example.invalid\"}");
        var second = QuotaParser.JsonObject("{\"type\":\"chatgpt\",\"email\":\"different@example.invalid\"}");
        string identity = QuotaParser.Fingerprint(first);
        Assert(identity != null && identity.Length == 12 && identity.IndexOf('@') < 0, "Expose only an anonymous account fingerprint.");
        Assert(identity != QuotaParser.Fingerprint(second), "Distinct identities invalidate prior account data.");
        Assert(QuotaParser.AuthType(null) == "signedOut" && QuotaParser.AuthType(QuotaParser.JsonObject("{\"type\":\"unexpected\"}")) == "unsupported", "Auth state is restricted to a known set.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("Quota test failed: " + message);
    }
}
