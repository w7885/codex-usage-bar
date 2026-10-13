param([string]$LibraryPath)
$ErrorActionPreference = 'Stop'
$probeSource = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class CodexDesktopReadOnlyProbe {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public class Result { public string Hwnd; public uint Pid; public int SessionId; public string ProcessName; public string ExecutablePath; public string WindowClass; public RECT Rect; public bool Visible; public bool Minimized; public bool IsForeground; public bool IsCodexPackage; }
  private delegate bool EnumWindowsProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll", SetLastError=true)] private static extern bool EnumWindows(EnumWindowsProc p, IntPtr l);
  [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder b, int n);
  [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
  [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr p, int flags, StringBuilder path, ref int size);
  [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr p);
  public static int VisibleWindowCount;
  public static bool EnumerationSucceeded;
  public static int EnumerationError;
  public static Result Foreground;
  private static Result Describe(IntPtr h) {
    uint pid; GetWindowThreadProcessId(h,out pid); string name=""; int session=-1;
    try { var p=Process.GetProcessById((int)pid); name=p.ProcessName; session=p.SessionId; p.Dispose(); } catch { }
    string path=""; IntPtr process=OpenProcess(0x1000,false,pid);
    if(process!=IntPtr.Zero) { try { var b=new StringBuilder(32768); int size=b.Capacity; if(QueryFullProcessImageName(process,0,b,ref size)) path=b.ToString(); } finally { CloseHandle(process); } }
    var cls=new StringBuilder(256); GetClassName(h,cls,cls.Capacity); RECT rect; GetWindowRect(h,out rect);
    bool codex=String.Equals(name,"ChatGPT",StringComparison.OrdinalIgnoreCase) && path.IndexOf("\\WindowsApps\\OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)>=0 && path.EndsWith("\\app\\ChatGPT.exe",StringComparison.OrdinalIgnoreCase);
    return new Result{Hwnd="0x"+h.ToInt64().ToString("X"),Pid=pid,SessionId=session,ProcessName=name,ExecutablePath=path,WindowClass=cls.ToString(),Rect=rect,Visible=IsWindowVisible(h),Minimized=IsIconic(h),IsForeground=h==GetForegroundWindow(),IsCodexPackage=codex};
  }
  public static List<Result> Read() {
    IntPtr previous=IntPtr.Zero;
    try { previous=SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch(EntryPointNotFoundException) { }
    try {
    VisibleWindowCount=0; Foreground=null; var results=new List<Result>();
    IntPtr fg=GetForegroundWindow(); if(fg!=IntPtr.Zero) Foreground=Describe(fg);
    EnumerationSucceeded=EnumWindows((h,p)=> { if(IsWindowVisible(h)) VisibleWindowCount++; var row=Describe(h); if(String.Equals(row.ProcessName,"ChatGPT",StringComparison.OrdinalIgnoreCase)) results.Add(row); return true; },IntPtr.Zero);
    EnumerationError=EnumerationSucceeded?0:Marshal.GetLastWin32Error(); return results;
    } finally { if(previous!=IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
  }
}
'@
Add-Type -TypeDefinition $probeSource
$probeWindows = @([CodexDesktopReadOnlyProbe]::Read())
$probeForeground = [CodexDesktopReadOnlyProbe]::Foreground
$probeCurrentProcess = [System.Diagnostics.Process]::GetCurrentProcess()
[pscustomobject]@{
    ProbeProcessId = $PID
    ProbeSessionId = $probeCurrentProcess.SessionId
    EnumerationSucceeded = [CodexDesktopReadOnlyProbe]::EnumerationSucceeded
    EnumerationError = [CodexDesktopReadOnlyProbe]::EnumerationError
    VisibleTopLevelWindowCount = [CodexDesktopReadOnlyProbe]::VisibleWindowCount
    Foreground = if ($null -eq $probeForeground) { $null } else { [pscustomobject]@{ Hwnd=$probeForeground.Hwnd; Pid=$probeForeground.Pid; SessionId=$probeForeground.SessionId; ProcessName=$probeForeground.ProcessName; WindowClass=$probeForeground.WindowClass; Rect=$probeForeground.Rect; IsCodexPackage=$probeForeground.IsCodexPackage } }
    ChatGPTTopLevelWindowCount = $probeWindows.Count
    ChatGPTWindows = @($probeWindows | Where-Object { $_.Visible })
    CodexVisibleWindowCount = @($probeWindows | Where-Object { $_.IsCodexPackage -and $_.Visible -and -not $_.Minimized }).Count
} | ConvertTo-Json -Depth 6
$probeCurrentProcess.Dispose()
if ($LibraryPath) {
    Add-Type -AssemblyName PresentationFramework
    Add-Type -Path $LibraryPath
    $moduleWindows = @([CodexQuota.NativeWindows]::GetCodexWindows())
    $moduleForeground = $null
    $moduleForegroundFound = [CodexQuota.NativeWindows]::TryGetForegroundCodexWindow([ref]$moduleForeground)
    [pscustomobject]@{
        NativeModule = 'CodexQuota.NativeWindows'
        CodexWindowCount = $moduleWindows.Count
        ForegroundFound = $moduleForegroundFound
        Windows = @($moduleWindows | ForEach-Object { [pscustomobject]@{ Hwnd=('0x{0:X}' -f $_.Handle.ToInt64()); Pid=$_.ProcessId; WindowClass=$_.WindowClass; Left=$_.Left; Top=$_.Top; Right=$_.Right; Bottom=$_.Bottom; Dpi=[CodexQuota.NativeWindows]::GetDpiForWindow($_.Handle) } })
    } | ConvertTo-Json -Depth 4
}
