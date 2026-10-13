using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace CodexQuota.Tests
{
    internal static class IntegrationTests
    {
        internal static void Run()
        {
            RunScenario("happy", HappyAndOwnedProcess);
            RunScenario("timeout-second", TimeoutPreservesCacheAndIgnoresLateReply);
            RunScenario("eof-second", EndOfStreamRecovers);
            RunScenario("switch-during-read", AccountSwitchDoesNotRepublishOldData);
            RunScenario("notification", NotificationReadsCompleteSnapshot);
            RunScenario("notification-during-read", NotificationReadsCompleteSnapshot);
            RunScenario("signed-out", SignOutClearsCachedData);
            RunScenario("api-key", UnsupportedAccountDoesNotClaimQuota);
            RunScenario("slow-initialize", DisposeDuringInitializationStopsChild);
        }

        private static void RunScenario(string scenario, Action<string> test)
        {
            string previousScenario = Environment.GetEnvironmentVariable("QUOTA_TEST_SCENARIO");
            string previousPidFile = Environment.GetEnvironmentVariable("QUOTA_TEST_PID_FILE");
            string testRoot = Path.Combine(Path.GetTempPath(), "CodexUsageBarTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(testRoot, "home"));
            try
            {
                Environment.SetEnvironmentVariable("QUOTA_TEST_SCENARIO", scenario);
                Environment.SetEnvironmentVariable("QUOTA_TEST_PID_FILE", Path.Combine(testRoot, "child.pid"));
                test(testRoot);
                Console.WriteLine("PASS integration: " + scenario);
            }
            finally
            {
                Environment.SetEnvironmentVariable("QUOTA_TEST_SCENARIO", previousScenario);
                Environment.SetEnvironmentVariable("QUOTA_TEST_PID_FILE", previousPidFile);
                string resolved = Path.GetFullPath(testRoot);
                string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (resolved.StartsWith(temporaryRoot + "CodexUsageBarTests_", StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(resolved, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static QuotaClient Client(string root, int timeoutMilliseconds)
        {
            return new QuotaClient(Assembly.GetEntryAssembly().Location, Path.Combine(root, "home"), timeoutMilliseconds);
        }

        private static QuotaSnapshot Read(QuotaClient client)
        {
            Task<QuotaSnapshot> operation = client.GetSnapshotAsync();
            if (!operation.Wait(10000)) throw new InvalidOperationException("Quota request did not finish within the integration-test deadline.");
            return operation.GetAwaiter().GetResult();
        }

        private static void HappyAndOwnedProcess(string root)
        {
            int childId;
            using (var client = Client(root, 2000))
            {
                QuotaSnapshot snapshot = Read(client);
                Check(snapshot.Status == QuotaStatus.Ready && snapshot.HasData,
                    "Expected a ready snapshot; status=" + snapshot.Status + "; error=" + snapshot.ErrorMessage + ".");
                Check(snapshot.FiveHour.RemainingPercent == 58 && snapshot.Weekly.RemainingPercent == 87, "Must select the codex bucket and calculate remaining percent.");
                Check(snapshot.LastUpdatedAt.HasValue && snapshot.AccountFingerprint != null, "Expected data time and account identity.");
                childId = ReadPid(root);
                Check(IsAlive(childId), "The fake app-server must be alive before disposal.");
            }
            Check(WaitForExit(childId, 2000), "Disposal left an owned app-server running.");
        }

        private static void TimeoutPreservesCacheAndIgnoresLateReply(string root)
        {
            using (var client = Client(root, 2000))
            {
                QuotaSnapshot initial = Read(client);
                Check(initial.Status == QuotaStatus.Ready, "The first read must succeed before testing stale data.");
                int lateDataPublished = 0;
                client.SnapshotUpdated += delegate(QuotaSnapshot update)
                {
                    if (update.Status == QuotaStatus.Ready && update.FiveHour != null && update.FiveHour.UsedPercent == 85)
                        Interlocked.Exchange(ref lateDataPublished, 1);
                };
                QuotaSnapshot failed = Read(client);
                Check(failed.Status == QuotaStatus.Error && failed.ErrorMessage == "quota_timeout", "A slow request must surface the safe timeout state.");
                Check(failed.FiveHour.RemainingPercent == 58 && failed.LastUpdatedAt == initial.LastUpdatedAt, "A failed refresh must preserve the last values and their actual data time.");
                Check(lateDataPublished == 0, "A timed-out response must not overwrite the cached snapshot.");
                Environment.SetEnvironmentVariable("QUOTA_TEST_SCENARIO", "happy");
                QuotaSnapshot recovered = Read(client);
                Check(recovered.Status == QuotaStatus.Ready && recovered.FiveHour.RemainingPercent == 58, "Client must recover using a fresh owned server after a timeout.");
            }
        }

        private static void EndOfStreamRecovers(string root)
        {
            using (var client = Client(root, 1500))
            {
                QuotaSnapshot initial = Read(client);
                QuotaSnapshot failed = Read(client);
                Check(failed.Status == QuotaStatus.Error && failed.HasData && failed.LastUpdatedAt == initial.LastUpdatedAt, "EOF must retain only the last successful data and time.");
                Environment.SetEnvironmentVariable("QUOTA_TEST_SCENARIO", "happy");
                QuotaSnapshot recovered = Read(client);
                Check(recovered.Status == QuotaStatus.Ready && recovered.LastUpdatedAt >= initial.LastUpdatedAt, "EOF must allow a later read to reconnect.");
            }
        }

        private static void AccountSwitchDoesNotRepublishOldData(string root)
        {
            using (var client = Client(root, 2000))
            using (var newAccountReady = new ManualResetEventSlim(false))
            {
                QuotaSnapshot initial = Read(client);
                int accountChanged = 0, oldAccountRepublished = 0;
                QuotaSnapshot newSnapshot = null;
                client.AuthChanged += delegate { Interlocked.Exchange(ref accountChanged, 1); };
                client.SnapshotUpdated += delegate(QuotaSnapshot update)
                {
                    if (update.Status != QuotaStatus.Ready) return;
                    if (Interlocked.CompareExchange(ref accountChanged, 0, 0) != 0 && update.AccountFingerprint == initial.AccountFingerprint)
                        Interlocked.Exchange(ref oldAccountRepublished, 1);
                    if (update.AccountFingerprint != null && update.AccountFingerprint != initial.AccountFingerprint)
                    {
                        newSnapshot = update;
                        newAccountReady.Set();
                    }
                };
                Read(client); // Server emits account/updated while this quota read is in flight.
                Check(newAccountReady.Wait(5000), "Account notification must trigger a complete refresh for the new identity.");
                Check(oldAccountRepublished == 0, "An in-flight response restored the old account's quota after the identity change.");
                Check(newSnapshot.FiveHour.RemainingPercent == 90 && newSnapshot.Weekly.RemainingPercent == 80, "The new identity must have its own complete snapshot.");
            }
        }

        private static void NotificationReadsCompleteSnapshot(string root)
        {
            using (var client = Client(root, 2000))
            using (var notificationReady = new ManualResetEventSlim(false))
            {
                QuotaSnapshot notificationSnapshot = null;
                client.SnapshotUpdated += delegate(QuotaSnapshot update)
                {
                    if (update.Status == QuotaStatus.Ready && update.FiveHour != null && update.FiveHour.UsedPercent == 44)
                    {
                        notificationSnapshot = update;
                        notificationReady.Set();
                    }
                };
                Check(Read(client).FiveHour.UsedPercent == 42, "Expected the initial full read.");
                Check(notificationReady.Wait(6500), "A notification within the debounce period must eventually refresh instead of being lost.");
                Check(notificationSnapshot.FiveHour.RemainingPercent == 56 && notificationSnapshot.Weekly.RemainingPercent == 83,
                    "Partial notifications must trigger a complete read, including the weekly window.");
            }
        }

        private static void SignOutClearsCachedData(string root)
        {
            using (var client = Client(root, 2000))
            {
                Check(Read(client).HasData, "Expected a cache before sign-out.");
                QuotaSnapshot signedOut = Read(client);
                Check(signedOut.Status == QuotaStatus.SignedOut && !signedOut.HasData && !signedOut.LastUpdatedAt.HasValue,
                    "A signed-out identity must not display the prior account's quota or data timestamp.");
            }
        }

        private static void UnsupportedAccountDoesNotClaimQuota(string root)
        {
            using (var client = Client(root, 2000))
            {
                QuotaSnapshot snapshot = Read(client);
                Check(snapshot.Status == QuotaStatus.UnsupportedAccount && !snapshot.HasData && !snapshot.LastUpdatedAt.HasValue,
                    "API-key accounts must not claim ChatGPT quota values.");
            }
        }

        private static void DisposeDuringInitializationStopsChild(string root)
        {
            var client = Client(root, 3000);
            Task<QuotaSnapshot> operation = client.GetSnapshotAsync();
            int childId = 0;
            try
            {
                for (int attempt = 0; attempt < 100 && childId == 0; attempt++)
                {
                    try { childId = ReadPid(root); } catch (IOException) { Thread.Sleep(20); }
                }
                Check(childId != 0, "The fake child did not initialize.");
                client.Dispose();
                Check(WaitForExit(childId, 2500), "Disposing during initialize left an owned child process alive.");
                try { Check(operation.Wait(5000), "Disposal must release a pending initialization request."); }
                catch (AggregateException error)
                {
                    Check(error.InnerException is ObjectDisposedException, "Unexpected error while disposing an initialization request.");
                }
            }
            finally { client.Dispose(); }
        }

        private static int ReadPid(string root)
        {
            int processId;
            if (!Int32.TryParse(File.ReadAllText(Path.Combine(root, "child.pid")), out processId))
                throw new IOException("The fake-server process id is not available yet.");
            return processId;
        }

        private static bool IsAlive(int processId)
        {
            try { using (var process = Process.GetProcessById(processId)) return !process.HasExited; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private static bool WaitForExit(int processId, int timeoutMilliseconds)
        {
            try { using (var process = Process.GetProcessById(processId)) return process.WaitForExit(timeoutMilliseconds); }
            catch (ArgumentException) { return true; }
            catch (InvalidOperationException) { return true; }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
