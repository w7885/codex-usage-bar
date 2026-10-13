using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexQuota.Tests
{
    // Invoked only when the test executable receives app-server arguments.
    internal static class FakeServer
    {
        private static readonly object outputLock = new object();
        private static readonly List<Task> delayedReplies = new List<Task>();
        private static readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

        internal static int Run(string[] args)
        {
            if (args.Length == 0 || args[0] != "app-server") return 2;
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
            string scenario = Environment.GetEnvironmentVariable("QUOTA_TEST_SCENARIO") ?? "happy";
            string pidFile = Environment.GetEnvironmentVariable("QUOTA_TEST_PID_FILE");
            if (!String.IsNullOrEmpty(pidFile)) File.WriteAllText(pidFile, Process.GetCurrentProcess().Id.ToString());
            int accountReads = 0, quotaReads = 0;
            bool identityChanged = false;
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                IDictionary<string, object> message;
                try { message = new JavaScriptSerializer().DeserializeObject(line) as IDictionary<string, object>; }
                catch (ArgumentException) { continue; }
                object id, methodValue;
                if (message == null || !message.TryGetValue("id", out id) || !message.TryGetValue("method", out methodValue)) continue;
                string method = methodValue as string;
                if (method == "initialize")
                {
                    if (scenario == "slow-initialize") ReplyAfter(id, new Dictionary<string, object>(), 1200);
                    else Reply(id, new Dictionary<string, object>());
                }
                else if (method == "account/read")
                {
                    accountReads++;
                    object account = scenario == "signed-out" && accountReads > 1 ? null :
                        (object)new Dictionary<string, object>
                        {
                            { "type", scenario == "api-key" ? "apiKey" : "chatgpt" },
                            { "email", identityChanged ? "second@example.invalid" : "first@example.invalid" },
                            { "planType", "plus" }
                        };
                    Reply(id, new Dictionary<string, object> { { "account", account } });
                }
                else if (method == "account/rateLimits/read")
                {
                    quotaReads++;
                    if (scenario == "eof-second" && quotaReads == 2) return 0;
                    if (scenario == "timeout-second" && quotaReads == 2)
                    {
                        ReplyAfter(id, Limits(85, 90), 3000);
                    }
                    else if (scenario == "switch-during-read" && quotaReads == 2)
                    {
                        identityChanged = true;
                        Notify("account/updated", new Dictionary<string, object> { { "authMode", "chatgpt" }, { "planType", "plus" } });
                        ReplyAfter(id, Limits(85, 90), 100);
                    }
                    else
                    {
                        if (scenario == "notification-during-read" && quotaReads == 1)
                        {
                            Notify("account/rateLimits/updated", new Dictionary<string, object>
                            {
                                { "rateLimits", new Dictionary<string, object> { { "primary", Window(99, 300) } } }
                            });
                        }
                        Reply(id, identityChanged ? Limits(10, 20) :
                            ((scenario == "notification" || scenario == "notification-during-read") && quotaReads > 1 ? Limits(44, 17) : Limits(42, 13)));
                        if (scenario == "notification" && quotaReads == 1)
                        {
                            delayedReplies.Add(Task.Run(async delegate
                            {
                                await Task.Delay(50).ConfigureAwait(false);
                                // Deliberately partial payload: clients must perform a complete read.
                                Notify("account/rateLimits/updated", new Dictionary<string, object>
                                {
                                    { "rateLimits", new Dictionary<string, object> { { "primary", Window(99, 300) } } }
                                });
                            }));
                        }
                    }
                }
                else Reply(id, new Dictionary<string, object>());
            }
            // Keep stdout open for a late response after the client closes stdin.
            // This exercises request-id isolation rather than relying on process death.
            Task.WaitAll(delayedReplies.ToArray(), 3500);
            return 0;
        }

        private static IDictionary<string, object> Window(double used, int minutes)
        {
            return new Dictionary<string, object>
            {
                { "usedPercent", used }, { "windowDurationMins", minutes }, { "resetsAt", 2000000000L }
            };
        }

        private static IDictionary<string, object> Limits(double fiveHourUsed, double weeklyUsed)
        {
            return new Dictionary<string, object>
            {
                { "rateLimitsByLimitId", new Dictionary<string, object>
                    {
                        { "codex", new Dictionary<string, object>
                            {
                                { "limitId", "codex" }, { "primary", Window(fiveHourUsed, 300) }, { "secondary", Window(weeklyUsed, 10080) }
                            }
                        },
                        { "codex_other", new Dictionary<string, object>
                            {
                                { "limitId", "codex_other" }, { "primary", Window(99, 300) }, { "secondary", Window(99, 10080) }
                            }
                        }
                    }
                },
                { "rateLimits", new Dictionary<string, object>
                    {
                        { "limitId", "codex_other" }, { "primary", Window(99, 300) }, { "secondary", Window(99, 10080) }
                    }
                }
            };
        }

        private static void ReplyAfter(object id, object result, int milliseconds)
        {
            delayedReplies.Add(Task.Run(async delegate
            {
                await Task.Delay(milliseconds).ConfigureAwait(false);
                Reply(id, result);
            }));
        }

        private static void Reply(object id, object result)
        {
            Send(new Dictionary<string, object> { { "id", id }, { "result", result } });
        }

        private static void Notify(string method, object parameters)
        {
            Send(new Dictionary<string, object> { { "method", method }, { "params", parameters } });
        }

        private static void Send(object message)
        {
            lock (outputLock) { Console.WriteLine(serializer.Serialize(message)); Console.Out.Flush(); }
        }
    }
}
