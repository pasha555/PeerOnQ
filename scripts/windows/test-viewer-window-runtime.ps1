#requires -Version 7.4
<#
.SYNOPSIS
Runtime acceptance for the real WinUI viewer window. Starts PeerOnQ.exe --viewer-self-test with the
GPU swap chain and with the bitmap fallback, captures the window's composed pixels (PrintWindow
with PW_RENDERFULLCONTENT, so an occluded window still captures its DirectComposition content) and
requires visible synthetic frames, every scale mode and a clean exit. Static XAML checks cannot see
runtime-only XAML failures; this does. No server, peer or network is used.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $ExecutablePath).Path
[IO.Directory]::CreateDirectory($EvidenceDirectory) | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PeerOnQWindowCapture
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
'@
$results = [Collections.Generic.List[object]]::new()
foreach ($renderer in @('gpu', 'bitmap')) {
    $resultPath = Join-Path $EvidenceDirectory "viewer-$renderer.json"
    $imagePath = Join-Path $EvidenceDirectory "viewer-$renderer.png"
    if (Test-Path -LiteralPath $resultPath) { throw 'Use a fresh evidence directory.' }
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.ArgumentList.Add('--viewer-self-test'); $start.ArgumentList.Add($resultPath)
    # Bitmap is the default renderer; the GPU swap chain is opt-in.
    if ($renderer -eq 'gpu') { $start.Environment['PEERONQ_VIEWER_RENDERER'] = 'gpu' } else { [void]$start.Environment.Remove('PEERONQ_VIEWER_RENDERER') }
    $process = [Diagnostics.Process]::Start($start)
    $varied = 0; $captured = $false
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while (-not $process.HasExited -and $process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 100; $process.Refresh()
        }
        if ($process.HasExited) { throw "$renderer self-test exited before showing a window (0x$('{0:X8}' -f $process.ExitCode))." }
        Start-Sleep -Milliseconds 1200
        $rect = [PeerOnQWindowCapture+RECT]::new()
        [void][PeerOnQWindowCapture]::GetWindowRect($process.MainWindowHandle, [ref]$rect)
        $width = $rect.Right - $rect.Left; $height = $rect.Bottom - $rect.Top
        $bitmap = [Drawing.Bitmap]::new($width, $height)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $hdc = $graphics.GetHdc()
        $captured = [PeerOnQWindowCapture]::PrintWindow($process.MainWindowHandle, $hdc, 2)
        $graphics.ReleaseHdc($hdc); $graphics.Dispose()
        $bitmap.Save($imagePath, [Drawing.Imaging.ImageFormat]::Png)
        # The synthetic frame is a red/green gradient; a blank or black surface has few colours.
        $colours = [Collections.Generic.HashSet[int]]::new()
        for ($y = [int]($height * 0.3); $y -lt [int]($height * 0.8); $y += 7) {
            for ($x = [int]($width * 0.2); $x -lt [int]($width * 0.8); $x += 7) { [void]$colours.Add($bitmap.GetPixel($x, $y).ToArgb()) }
        }
        $varied = $colours.Count
        $bitmap.Dispose()
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw "$renderer self-test timed out." }
    } finally { if (-not $process.HasExited) { $process.Kill($true) } }
    $exitCode = $process.ExitCode
    if (-not (Test-Path -LiteralPath $resultPath)) { throw "$renderer self-test crashed or wrote no result (exit 0x$('{0:X8}' -f $exitCode))." }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    $expectedRenderer = if ($renderer -eq 'gpu') { 'GPU swap chain' } else { 'bitmap' }
    $passed = $exitCode -eq 0 -and $result.passed -and $result.renderer -eq $expectedRenderer -and $captured -and $varied -ge 50 -and @($result.steps) -contains 'indicator' -and @($result.steps) -contains 'mode:3'
    $results.Add([ordered]@{ renderer = $renderer; exitCode = $exitCode; selfTest = $result; windowCaptured = $captured; distinctColours = $varied; image = $imagePath; passed = $passed })
    if (-not $passed) { $results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'result.json'); throw "$renderer viewer runtime acceptance failed (exit $exitCode, colours $varied, renderer $($result.renderer))." }
}
$results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'result.json')
Write-Output "Viewer window runtime acceptance passed: GPU swap chain and bitmap fallback both rendered visible frames and exited cleanly."
