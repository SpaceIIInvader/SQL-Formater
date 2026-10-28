param([switch]$BuildOnly)
$ErrorActionPreference = 'Stop'
$desktopRoot = Split-Path -Parent $PSScriptRoot
$kernelFolder = Join-Path $desktopRoot 'src'
$desktopSource = Join-Path $kernelFolder 'Desktop.cs'
$formatterSource = Join-Path $kernelFolder 'formatter.js'
$workerSource = Join-Path $kernelFolder 'desktop-worker.cjs'
$presetFile = Join-Path $desktopRoot 'presets\preset_ibeexpert.ini'
$licenseFile = Join-Path $desktopRoot 'licenses\node-license.txt'
$runtimeArchive = Join-Path $PSScriptRoot 'node.gz'
$buildInfo = Join-Path $PSScriptRoot 'BuildInfo.cs'
$builtExe = Join-Path $PSScriptRoot 'SQLAtelier.build.exe'
$applicationExe = Join-Path $desktopRoot 'SQLAtelier.exe'
$nodePath = (Get-Command node.exe -ErrorAction Stop).Source
$nodeHash = (Get-FileHash -LiteralPath $nodePath -Algorithm SHA256).Hash
if (!(Test-Path -LiteralPath $licenseFile)) { throw 'Node.js license file is missing' }
$inputStream = [IO.File]::OpenRead($nodePath)
$outputStream = [IO.File]::Create($runtimeArchive)
$gzip = [IO.Compression.GZipStream]::new($outputStream, [IO.Compression.CompressionLevel]::Optimal)
try { $inputStream.CopyTo($gzip) } finally { $gzip.Dispose(); $outputStream.Dispose(); $inputStream.Dispose() }
$buildVersion = (Get-FileHash -LiteralPath $workerSource -Algorithm SHA256).Hash.Substring(0, 10) + '-' + (Get-FileHash -LiteralPath $formatterSource -Algorithm SHA256).Hash.Substring(0, 10)
$buildSource = 'public static class BuildInfo { public const string NodeHash = "' + $nodeHash + '"; public const string Version = "' + $buildVersion + '"; }'
[IO.File]::WriteAllText($buildInfo, $buildSource, [Text.UTF8Encoding]::new($false))
$compilerArgs = @(
  '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001',
  "/out:$builtExe", '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
  '/reference:System.Web.Extensions.dll', '/reference:System.Core.dll',
  "/resource:$runtimeArchive,node.gz", "/resource:$formatterSource,formatter.js",
  "/resource:$workerSource,worker.cjs", "/resource:$presetFile,preset.ini",
  "/resource:$licenseFile,node-license.txt", $desktopSource, $buildInfo
)
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed' }
if ($BuildOnly) {
  Get-Item -LiteralPath $builtExe | Format-List FullName,Length
} else {
  Copy-Item -LiteralPath $builtExe -Destination $applicationExe -Force -ErrorAction Stop
  Get-Item -LiteralPath $applicationExe | Format-List FullName,Length
}
