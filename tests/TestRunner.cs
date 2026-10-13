using System;
using CodexQuota.Tests;

namespace CodexQuota
{
    internal static class TestRunner
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "app-server") return FakeServer.Run(args);
            try
            {
                QuotaTests.Run();
                LayoutTests.Run();
                PlatformSettingsTests.Run();
                IntegrationTests.Run();
                Console.WriteLine("ALL TESTS PASSED"); return 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message); return 1; }
        }
    }
}
