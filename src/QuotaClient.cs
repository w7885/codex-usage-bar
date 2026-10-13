using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexQuota
{
    public sealed class QuotaClient : IDisposable
    {
        private readonly string executablePath;
        private readonly string codexHomePath;
        private readonly SemaphoreSlim refreshGate = new SemaphoreSlim(1, 1);
        private readonly object sync = new object();
        private readonly object writeSync = new object();
        private readonly int requestTimeoutMilliseconds;
        private readonly FileSystemWatcher authWatcher;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<IDictionary<string, object>>> pending = new ConcurrentDictionary<int, TaskCompletionSource<IDictionary<string, object>>>();
        private Process process;
        private StreamWriter input;
        private int nextId;
        private int refreshScheduled;
        private int authGeneration;
        private long revision;
        private volatile bool disposed;
        private volatile bool connectionBroken;
        private string authStamp;
        private DateTime lastRefreshStarted = DateTime.MinValue;
        private QuotaSnapshot snapshot = new QuotaSnapshot { Status = QuotaStatus.Loading };

        public event Action<QuotaSnapshot> SnapshotUpdated;
        public event Action AuthChanged;

        public QuotaClient() : this(FindCodexExecutable(), null) { }

        public QuotaClient(string codexExecutablePath, string codexHomePath, int requestTimeoutMs = 30000)
        {
            if (requestTimeoutMs < 1) throw new ArgumentOutOfRangeException("requestTimeoutMs");
            executablePath = codexExecutablePath;
            this.codexHomePath = String.IsNullOrWhiteSpace(codexHomePath) ? FindCodexHome() : codexHomePath;
            requestTimeoutMilliseconds = requestTimeoutMs;
            try
            {
                if (Directory.Exists(this.codexHomePath))
                {
                    authWatcher = new FileSystemWatcher(this.codexHomePath, "auth.json");
                    authWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
                    authWatcher.Changed += AuthMetadataChanged;
                    authWatcher.Created += AuthMetadataChanged;
                    authWatcher.Deleted += AuthMetadataChanged;
                    authWatcher.Renamed += AuthMetadataChanged;
                    authWatcher.EnableRaisingEvents = true;
                }
            }
            catch (Exception) { if (authWatcher != null) authWatcher.Dispose(); authWatcher = null; }
        }

        public static string FindCodexHome()
        {
            var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
            return String.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : configured;
        }

        public static string FindCodexExecutable()
        {
            var candidates = new List<string>();
            var userBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            AddExecutable(candidates, Path.Combine(userBin, "codex.exe"));
            foreach (var folder in SafeDirectories(userBin, "*")) AddExecutable(candidates, Path.Combine(folder, "codex.exe"));
            var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            foreach (var folder in SafeDirectories(windowsApps, "*Codex*"))
            {
                AddExecutable(candidates, Path.Combine(folder, "app", "resources", "codex.exe"));
                AddExecutable(candidates, Path.Combine(folder, "resources", "codex.exe"));
            }
            return candidates.OrderByDescending(SafeWriteTime).FirstOrDefault();
        }

        private static IEnumerable<string> SafeDirectories(string path, string pattern)
        {
            try { return Directory.Exists(path) ? Directory.GetDirectories(path, pattern) : new string[0]; }
            catch (Exception) { return new string[0]; }
        }

        private static void AddExecutable(ICollection<string> candidates, string path)
        {
            try { if (File.Exists(path) && new FileInfo(path).Length > 0) candidates.Add(path); }
            catch (Exception) { }
        }

        private static DateTime SafeWriteTime(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch (Exception) { return DateTime.MinValue; }
        }

        public async Task<QuotaSnapshot> GetSnapshotAsync()
        {
            if (disposed) throw new ObjectDisposedException("QuotaClient");
            await refreshGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (disposed) throw new ObjectDisposedException("QuotaClient");
                lock (sync) lastRefreshStarted = DateTime.UtcNow;
                await EnsureConnectedAsync().ConfigureAwait(false);
                for (int authAttempt = 0; authAttempt < 3; authAttempt++)
                {
                    int accountGeneration = Interlocked.CompareExchange(ref authGeneration, 0, 0);
                    var accountResult = await RequestAsync("account/read", new Dictionary<string, object> { { "refreshToken", false } }).ConfigureAwait(false);
                    if (accountGeneration != Interlocked.CompareExchange(ref authGeneration, 0, 0)) continue;
                    var account = QuotaParser.AsObject(QuotaParser.Get(accountResult, "account"));
                    var auth = QuotaParser.AuthType(account);
                    var fingerprint = QuotaParser.Fingerprint(account);
                    var previous = CurrentSnapshot();
                    bool changed = previous.AuthType != null && (previous.AuthType != auth || previous.AccountFingerprint != fingerprint);
                    if (changed)
                    {
                        PublishForAccount(new QuotaSnapshot { Status = QuotaStatus.Loading, AuthType = auth, AccountFingerprint = fingerprint }, accountGeneration);
                        NotifyAuthChanged();
                    }
                    if (auth == "signedOut") return PublishForAccount(new QuotaSnapshot { Status = QuotaStatus.SignedOut, AuthType = auth }, accountGeneration);
                    if (auth != "chatgpt" && auth != "chatgptAuthTokens") return PublishForAccount(new QuotaSnapshot { Status = QuotaStatus.UnsupportedAccount, AuthType = auth, AccountFingerprint = fingerprint }, accountGeneration);
                    var limits = await RequestAsync("account/rateLimits/read", null).ConfigureAwait(false);
                    if (accountGeneration != Interlocked.CompareExchange(ref authGeneration, 0, 0)) continue;
                    return PublishForAccount(QuotaParser.ParseLimits(limits, auth, fingerprint, DateTimeOffset.UtcNow), accountGeneration);
                }
                ScheduleRefresh(true);
                return CurrentSnapshot();
            }
            catch (ObjectDisposedException) { throw; }
            catch (Exception exception)
            {
                StopOwnedProcess();
                return Publish(CurrentSnapshot().WithStatus(QuotaStatus.Error, SafeError(exception)));
            }
            finally { refreshGate.Release(); }
        }

        private async Task EnsureConnectedAsync()
        {
            var stamp = ReadAuthStamp();
            bool restart = connectionBroken || (authStamp != null && stamp != authStamp);
            if (restart)
            {
                // Only file metadata is examined. Credentials are owned by Codex.
                if (authStamp != null && stamp != authStamp)
                {
                    InvalidateAuth();
                }
                StopOwnedProcess();
            }
            if (process != null)
            {
                try { if (!process.HasExited) return; }
                catch (InvalidOperationException) { }
                StopOwnedProcess();
            }
            if (String.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) throw new QuotaClientException("codex_not_found");
            var start = new ProcessStartInfo
            {
                FileName = executablePath, Arguments = "app-server --listen stdio://",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            start.EnvironmentVariables["CODEX_HOME"] = codexHomePath;
            var ownProcess = new Process { StartInfo = start };
            ownProcess.ErrorDataReceived += delegate { }; // Drain diagnostics without retaining or logging secrets.
            lock (sync)
            {
                if (disposed) { ownProcess.Dispose(); throw new ObjectDisposedException("QuotaClient"); }
                if (!ownProcess.Start()) { ownProcess.Dispose(); throw new QuotaClientException("codex_start_failed"); }
                process = ownProcess;
                lock (writeSync) input = new StreamWriter(ownProcess.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
                connectionBroken = false;
                authStamp = stamp;
                ownProcess.BeginErrorReadLine();
            }
            var readTask = Task.Run(() => ReadLoopAsync(ownProcess));
            await RequestAsync("initialize", new Dictionary<string, object>
            {
                { "clientInfo", new Dictionary<string, object> { { "name", "codex_quota_bar" }, { "title", "Codex Quota Bar" }, { "version", "1.0.0" } } }
            }).ConfigureAwait(false);
            SendMessage(new Dictionary<string, object> { { "method", "initialized" }, { "params", new Dictionary<string, object>() } });
        }

        private string ReadAuthStamp()
        {
            try
            {
                var file = new FileInfo(Path.Combine(codexHomePath, "auth.json"));
                return file.Exists ? file.LastWriteTimeUtc.Ticks.ToString() + ":" + file.Length.ToString() : "absent";
            }
            catch (Exception) { return "unavailable"; }
        }

        private async Task<IDictionary<string, object>> RequestAsync(string method, IDictionary<string, object> parameters)
        {
            int id = Interlocked.Increment(ref nextId);
            var completion = new TaskCompletionSource<IDictionary<string, object>>();
            pending[id] = completion;
            try
            {
                var message = new Dictionary<string, object> { { "id", id }, { "method", method } };
                if (parameters != null) message["params"] = parameters;
                SendMessage(message);
                var completed = await Task.WhenAny(completion.Task, Task.Delay(requestTimeoutMilliseconds)).ConfigureAwait(false);
                if (completed != completion.Task) throw new TimeoutException();
                return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                TaskCompletionSource<IDictionary<string, object>> ignored;
                pending.TryRemove(id, out ignored);
            }
        }

        private void SendMessage(IDictionary<string, object> message)
        {
            string json = new JavaScriptSerializer().Serialize(message);
            lock (writeSync)
            {
                if (input == null || connectionBroken) throw new IOException();
                input.WriteLine(json);
                input.Flush();
            }
        }

        private async Task ReadLoopAsync(Process ownProcess)
        {
            try
            {
                while (!disposed)
                {
                    string line = await ownProcess.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    IDictionary<string, object> message;
                    try { message = QuotaParser.JsonObject(line); }
                    catch (Exception) { continue; }
                    if (message == null) continue;
                    object idValue = QuotaParser.Get(message, "id");
                    int id;
                    if (idValue != null && Int32.TryParse(Convert.ToString(idValue), out id))
                    {
                        TaskCompletionSource<IDictionary<string, object>> completion;
                        if (!pending.TryRemove(id, out completion)) continue;
                        var error = QuotaParser.AsObject(QuotaParser.Get(message, "error"));
                        if (error != null) completion.TrySetException(new QuotaClientException(SafeRpcError(error)));
                        else
                        {
                            var result = QuotaParser.AsObject(QuotaParser.Get(message, "result"));
                            if (result == null) completion.TrySetException(new QuotaClientException("quota_invalid_response"));
                            else completion.TrySetResult(result);
                        }
                        continue;
                    }
                    var method = QuotaParser.Text(QuotaParser.Get(message, "method"));
                    if (method == "account/rateLimits/updated") ScheduleRefresh(false);
                    else if (method == "account/updated")
                    {
                        InvalidateAuth();
                        ScheduleRefresh(true);
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                if (ReferenceEquals(process, ownProcess))
                {
                    connectionBroken = true;
                    FailPending();
                    if (!disposed) Publish(CurrentSnapshot().WithStatus(QuotaStatus.Error, "quota_disconnected"));
                }
            }
        }

        private void ScheduleRefresh(bool force)
        {
            if (disposed) return;
            // Queue a trailing full read even when another read is in progress;
            // its response may already have been produced before this notification.
            if (Interlocked.CompareExchange(ref refreshScheduled, 1, 0) != 0) return;
            Task.Run(async delegate
            {
                try
                {
                    double delay;
                    lock (sync) delay = force ? 0 : (lastRefreshStarted.AddSeconds(5) - DateTime.UtcNow).TotalMilliseconds;
                    if (delay > 0) await Task.Delay((int)Math.Min(delay, 5000)).ConfigureAwait(false);
                    await GetSnapshotAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { }
                finally { Interlocked.Exchange(ref refreshScheduled, 0); }
            });
        }

        private QuotaSnapshot CurrentSnapshot() { lock (sync) return snapshot; }

        private QuotaSnapshot Publish(QuotaSnapshot value)
        {
            lock (sync) { value.Revision = ++revision; snapshot = value; }
            NotifySnapshot(value);
            return value;
        }

        private QuotaSnapshot PublishForAccount(QuotaSnapshot value, int generation)
        {
            lock (sync)
            {
                if (generation != authGeneration) return snapshot;
                value.Revision = ++revision;
                snapshot = value;
            }
            NotifySnapshot(value);
            return value;
        }

        private void NotifySnapshot(QuotaSnapshot value)
        {
            var handler = SnapshotUpdated;
            if (handler != null) foreach (Action<QuotaSnapshot> subscriber in handler.GetInvocationList()) { try { subscriber(value); } catch (Exception) { } }
        }

        private void NotifyAuthChanged()
        {
            var handler = AuthChanged;
            if (handler != null) foreach (Action subscriber in handler.GetInvocationList()) { try { subscriber(); } catch (Exception) { } }
        }

        private void InvalidateAuth()
        {
            var invalidated = new QuotaSnapshot { Status = QuotaStatus.Loading };
            lock (sync)
            {
                authGeneration++;
                invalidated.Revision = ++revision;
                snapshot = invalidated;
            }
            NotifySnapshot(invalidated);
            NotifyAuthChanged();
        }

        private void AuthMetadataChanged(object sender, FileSystemEventArgs args)
        {
            if (disposed) return;
            InvalidateAuth();
            ScheduleRefresh(true);
        }

        private static string SafeRpcError(IDictionary<string, object> error)
        {
            var code = QuotaParser.Get(error, "code");
            if (code is int || code is long) return "quota_rpc_" + Convert.ToString(code, System.Globalization.CultureInfo.InvariantCulture);
            return "quota_rpc_error";
        }

        private static string SafeError(Exception error)
        {
            var known = error as QuotaClientException;
            if (known != null) return known.Message;
            if (error is TimeoutException) return "quota_timeout";
            if (error is IOException) return "quota_disconnected";
            return "quota_read_failed";
        }

        private void FailPending()
        {
            foreach (var pair in pending)
            {
                TaskCompletionSource<IDictionary<string, object>> completion;
                if (pending.TryRemove(pair.Key, out completion)) completion.TrySetException(new IOException());
            }
        }

        private void StopOwnedProcess()
        {
            Process ownProcess;
            lock (sync)
            {
                ownProcess = process;
                process = null;
                connectionBroken = true;
            }
            lock (writeSync)
            {
                if (input != null) { try { input.Close(); } catch (Exception) { } input = null; }
            }
            FailPending();
            if (ownProcess == null) return;
            try
            {
                if (!ownProcess.HasExited && !ownProcess.WaitForExit(2000))
                {
                    // Kill only this client's child tree; the desktop client is never targeted.
                    var killerStart = new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"),
                        Arguments = "/PID " + ownProcess.Id.ToString() + " /T /F", UseShellExecute = false,
                        CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    using (var killer = Process.Start(killerStart))
                    {
                        if (killer != null)
                        {
                            killer.BeginOutputReadLine();
                            killer.BeginErrorReadLine();
                            if (!killer.WaitForExit(5000)) { try { killer.Kill(); killer.WaitForExit(2000); } catch (Exception) { } }
                        }
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                try { if (!ownProcess.HasExited) { ownProcess.Kill(); ownProcess.WaitForExit(2000); } }
                catch (Exception) { }
                ownProcess.Dispose();
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
            }
            if (authWatcher != null) authWatcher.Dispose();
            StopOwnedProcess();
            // Do not dispose refreshGate while an awaited request may still release it.
        }

        private sealed class QuotaClientException : Exception
        {
            internal QuotaClientException(string safeMessage) : base(safeMessage) { }
        }
    }
}
