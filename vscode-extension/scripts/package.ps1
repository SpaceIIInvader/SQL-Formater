param()
$ErrorActionPreference = 'Stop'
$extensionRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$manifest = Get-Content -LiteralPath (Join-Path $extensionRoot 'package.json') -Raw | ConvertFrom-Json
$outputFolder = Join-Path $extensionRoot 'dist'
[IO.Directory]::CreateDirectory($outputFolder) | Out-Null
$stage = Join-Path $outputFolder ('.stage-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
$packageFile = Join-Path $outputFolder ($manifest.name + '-' + $manifest.version + '.vsix')
function Escape-Xml([string]$value) { return [Security.SecurityElement]::Escape($value) }
try {
    $packageManifest = @"
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
  <Metadata>
    <Identity Language="en-US" Id="$(Escape-Xml $manifest.name)" Version="$(Escape-Xml $manifest.version)" Publisher="$(Escape-Xml $manifest.publisher)" />
    <DisplayName>$(Escape-Xml $manifest.displayName)</DisplayName>
    <Description xml:space="preserve">$(Escape-Xml $manifest.description)</Description>
    <Tags>sql,firebird,psql,formatter</Tags><Categories>Formatters</Categories>
    <Properties>
      <Property Id="Microsoft.VisualStudio.Code.Engine" Value="$(Escape-Xml $manifest.engines.vscode)" />
      <Property Id="Microsoft.VisualStudio.Code.ExtensionDependencies" Value="" />
      <Property Id="Microsoft.VisualStudio.Code.ExtensionPack" Value="" />
    </Properties>
  </Metadata>
  <Installation><InstallationTarget Id="Microsoft.VisualStudio.Code" /></Installation>
  <Dependencies />
  <Assets>
    <Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true" />
    <Asset Type="Microsoft.VisualStudio.Services.Content.Details" Path="extension/README.md" Addressable="true" />
    <Asset Type="Microsoft.VisualStudio.Services.Content.Changelog" Path="extension/CHANGELOG.md" Addressable="true" />
  </Assets>
</PackageManifest>
"@
    $contentTypes = @'
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="json" ContentType="application/json" />
  <Default Extension="js" ContentType="application/javascript" />
  <Default Extension="cjs" ContentType="application/javascript" />
  <Default Extension="md" ContentType="text/markdown" />
  <Default Extension="ini" ContentType="text/plain" />
  <Default Extension="vsixmanifest" ContentType="text/xml" />
</Types>
'@
    [IO.File]::WriteAllText((Join-Path $stage 'extension.vsixmanifest'), $packageManifest, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $stage '[Content_Types].xml'), $contentTypes, [Text.UTF8Encoding]::new($false))
    $files = @('package.json', 'extension.cjs', 'README.md', 'CHANGELOG.md', 'vendor/formatter.js', 'presets/preset_ibeexpert.ini')
    foreach ($script in Get-ChildItem -LiteralPath (Join-Path $extensionRoot 'lib') -File -Filter '*.cjs') { $files += 'lib/' + $script.Name }
    foreach ($relative in $files) {
        $destination = Join-Path $stage ('extension/' + $relative)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $extensionRoot $relative) -Destination $destination
    }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $temporaryPackage = Join-Path $stage 'package.zip'
    $zip = [IO.Compression.ZipFile]::Open($temporaryPackage, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
            if ($file.FullName -eq $temporaryPackage) { continue }
            $entry = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
    Copy-Item -LiteralPath $temporaryPackage -Destination $packageFile -Force
    Get-Item -LiteralPath $packageFile | Format-List FullName,Length
} finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    if (!$resolvedStage.StartsWith($outputFolder + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging cleanup path' }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
