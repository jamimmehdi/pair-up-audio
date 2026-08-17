param(
    [Parameter(Mandatory)] [string]$ProcessName,
    [int]$X = 60, [int]$Y = 60, [int]$Width = 1400, [int]$Height = 940
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinApiRz {
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint flags);
}
"@

$proc = Get-Process -Name $ProcessName -ErrorAction Stop
[WinApiRz]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, $X, $Y, $Width, $Height, 0x0040) | Out-Null
Start-Sleep -Milliseconds 300
[WinApiRz]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 300
"resized"
