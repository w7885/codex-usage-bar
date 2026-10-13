using System;

namespace CodexQuota
{
    internal static class LayoutTests
    {
        internal static void Run()
        {
            var first = OverlayPlacement.Calculate(0, 0, 1536, 960, 96, 376, 40, 0, 8);
            Assert(first.Left == 580 && first.Top == 8 && first.Width == 376 && first.Height == 40, "Centered placement");
            var second = OverlayPlacement.Calculate(-1920, 0, 0, 1080, 144, 376, 40, 0, 8);
            Assert(second.Left == -1242 && second.Top == 12 && second.Width == 564 && second.Height == 60, "Negative monitor coordinates and 150% DPI");
            var clamped = OverlayPlacement.Calculate(0, 0, 600, 400, 192, 376, 40, 1000, 1000);
            Assert(clamped.Left == 0 && clamped.Top == 320 && clamped.Width == 600 && clamped.Height == 80, "Constrain placement to target window");
            Assert(!NativeWindows.IsCodexExecutablePath(@"C:\Program Files\WindowsApps\OpenAI.ChatGPT_1.0\app\ChatGPT.exe"), "Reject ordinary ChatGPT window");
            Assert(NativeWindows.IsCodexExecutablePath(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1007\app\ChatGPT.exe"), "Recognize Codex package independent of version");
            Console.WriteLine("PASS layout: DPI, negative coordinates, bounds, window identity");
        }
        static void Assert(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    }
}
