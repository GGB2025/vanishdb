$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework 4.x C# compiler was not found.'
}

$source = Join-Path $PSScriptRoot 'src'
$output = Join-Path $PSScriptRoot 'LocalImageRepair.exe'
$manifest = Join-Path $source 'app.manifest'
$icon = Join-Path $source 'app.ico'
$mask = Join-Path $source 'watermark-mask.bin'
$grayMask = Join-Path $source 'gray-watermark-mask.bin'
$outlineMask = Join-Path $source 'outlined-watermark-mask.bin'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /codepage:65001 `
    "/out:$output" "/win32manifest:$manifest" "/win32icon:$icon" "/resource:$mask,WatermarkMask" `
    "/resource:$grayMask,GrayWatermarkMask" `
    "/resource:$outlineMask,OutlinedWatermarkMask" `
    /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    (Join-Path $source 'Main.cs') (Join-Path $source 'InterfaceTheme.cs') `
    (Join-Path $source 'WatermarkRepair.cs') (Join-Path $source 'OverlapTexture.cs')
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
Write-Host $output
