param([string]$Executable, [string]$OutputPath)

Add-Type -AssemblyName System.Drawing.Common
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowCaptureNative {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
}
'@
$process = Start-Process -FilePath $Executable -WindowStyle Hidden -PassThru
try {
  Start-Sleep -Seconds 7
  $process.Refresh()
  if ($process.HasExited -or $process.MainWindowHandle -eq 0) { throw 'Application window did not initialize.' }
  $rect = [WindowCaptureNative+RECT]::new()
  [WindowCaptureNative]::GetWindowRect($process.MainWindowHandle, [ref]$rect) | Out-Null
  $scale = [WindowCaptureNative]::GetDpiForWindow($process.MainWindowHandle) / 96.0
  $width = [Math]::Max(1, [int](($rect.Right - $rect.Left) * $scale))
  $height = [Math]::Max(1, [int](($rect.Bottom - $rect.Top) * $scale))
  $bitmap = [System.Drawing.Bitmap]::new($width, $height)
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  $hdc = $graphics.GetHdc()
  [WindowCaptureNative]::PrintWindow($process.MainWindowHandle, $hdc, 2) | Out-Null
  $graphics.ReleaseHdc($hdc)
  [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
  $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
  $graphics.Dispose()
  $bitmap.Dispose()
} finally {
  if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
