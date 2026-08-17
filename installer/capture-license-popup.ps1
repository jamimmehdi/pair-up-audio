Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class LicCap {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@

function Click($x, $y) {
    [LicCap]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 150
    [LicCap]::mouse_event(0x02, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [LicCap]::mouse_event(0x04, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}

$proc = Get-Process PairUp.App
[LicCap]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, 60, 60, 1400, 940, 0x0040) | Out-Null
Start-Sleep -Milliseconds 300
[LicCap]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 300

Click 1312 80    # credit/info button
Click 1130 312   # License (MIT) row inside the credit popup

$rect = New-Object LicCap+RECT
[LicCap]::GetWindowRect($proc.MainWindowHandle, [ref]$rect) | Out-Null
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
$bmp = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bmp)
$graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, [System.Drawing.Size]::new($width, $height))
$bmp.Save("D:\PairUp\docs\screenshots\_check.png")
$graphics.Dispose()
$bmp.Dispose()
Write-Host "done"
