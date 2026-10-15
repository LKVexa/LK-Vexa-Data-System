# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 RUSSELL PHILIP SMITHSON
[CmdletBinding()]
param(
    [string]$SourceDirectory = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$')]
    [string]$PackageName = 'LKVS-3.0.1-Windows-x64-candidate'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Run this packaging script on Windows.' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$jpPackageSource = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\')
$jpPackageOutput = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$jpPackageUtf8 = New-Object Text.UTF8Encoding($false)
$jpPackageChecker = Join-Path $PSScriptRoot 'Test-WindowsPaths.ps1'

function Assert-JPPackageFile([string]$Relative) {
    $jpFull = Join-Path $jpPackageSource $Relative
    if (-not [IO.File]::Exists($jpFull)) { throw ('Required package input is missing: ' + $Relative) }
    $jpProbe = $jpPackageSource
    foreach ($jpComponent in $Relative.Replace('\', '/').Split('/')) {
        $jpProbe = Join-Path $jpProbe $jpComponent
        if (([IO.File]::GetAttributes($jpProbe) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Reparse-point package input is forbidden: ' + $Relative) }
    }
}

function Get-JPPackageDigest([string]$Path) {
    $jpHashAlgorithm = [Security.Cryptography.SHA256]::Create()
    $jpHashStream = [IO.File]::OpenRead($Path)
    try { return ([BitConverter]::ToString($jpHashAlgorithm.ComputeHash($jpHashStream))).Replace('-', '').ToLowerInvariant() }
    finally { $jpHashStream.Dispose(); $jpHashAlgorithm.Dispose() }
}

function Get-JPPackageTreeFiles([string]$RelativeDirectory) {
    $jpTreeRoot = Join-Path $jpPackageSource $RelativeDirectory
    if (-not [IO.Directory]::Exists($jpTreeRoot)) { throw ('Required package directory is missing: ' + $RelativeDirectory) }
    $jpPending = New-Object 'Collections.Generic.Queue[string]'
    $jpPending.Enqueue($jpTreeRoot)
    $jpFound = New-Object 'Collections.Generic.List[string]'
    while ($jpPending.Count -gt 0) {
        $jpCurrentDirectory = $jpPending.Dequeue()
        if (([IO.File]::GetAttributes($jpCurrentDirectory) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Reparse-point package directory is forbidden: ' + $jpCurrentDirectory) }
        foreach ($jpPath in [IO.Directory]::EnumerateFileSystemEntries($jpCurrentDirectory)) {
            $jpAttributes = [IO.File]::GetAttributes($jpPath)
            if (($jpAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Reparse-point package input is forbidden: ' + $jpPath) }
            if (($jpAttributes -band [IO.FileAttributes]::Directory) -ne 0) { $jpPending.Enqueue($jpPath) }
            else { $jpFound.Add($jpPath.Substring($jpPackageSource.Length + 1).Replace('\', '/')) }
        }
    }
    return $jpFound.ToArray()
}

function Assert-JPBuildReceipt([string[]]$ExpectedArtifacts) {
    Assert-JPPackageFile 'bin/BUILD-RECEIPT.json'
    $jpReceipt = [IO.File]::ReadAllText((Join-Path $jpPackageSource 'bin/BUILD-RECEIPT.json')) | ConvertFrom-Json
    if ($jpReceipt.format -ne 'lkvs-build-receipt/1') { throw 'The build receipt format is unsupported. Run scripts/Build-Windows.ps1 first.' }
    if ($jpReceipt.product_version -ne [IO.File]::ReadAllText((Join-Path $jpPackageSource 'VERSION')).Trim()) { throw 'The build receipt version does not match VERSION. Rebuild the source.' }
    $jpActualSource = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($jpRelative in @(Get-JPPackageTreeFiles 'native') + @(Get-JPPackageTreeFiles 'host')) {
        if ($jpRelative -match '^native/.+\.go$' -or $jpRelative -eq 'native/go.mod' -or $jpRelative -match '^host/.+\.cs$') { [void]$jpActualSource.Add($jpRelative) }
    }
    $jpReceiptSource = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $jpReceiptArtifacts = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($jpCategory in @('source_files', 'artifact_files')) {
        $jpEntries = @($jpReceipt.$jpCategory)
        if ($jpEntries.Count -eq 0) { throw ('The build receipt has no ' + $jpCategory + '. Rebuild the source.') }
        foreach ($jpEntry in $jpEntries) {
            $jpRelative = [string]$jpEntry.path
            if ($jpCategory -eq 'source_files') {
                if (-not $jpActualSource.Contains($jpRelative) -or -not $jpReceiptSource.Add($jpRelative)) { throw ('Unexpected or duplicate build source in receipt: ' + $jpRelative) }
            }
            else {
                if ($jpRelative -notin $ExpectedArtifacts -or -not $jpReceiptArtifacts.Add($jpRelative)) { throw ('Unexpected or duplicate binary in receipt: ' + $jpRelative) }
            }
            Assert-JPPackageFile $jpRelative
            $jpFull = Join-Path $jpPackageSource $jpRelative
            if (([IO.FileInfo]$jpFull).Length -ne [long]$jpEntry.bytes -or (Get-JPPackageDigest $jpFull) -ne $jpEntry.sha256) { throw ('Build inputs or binaries changed after compilation: ' + $jpRelative + '. Rebuild with scripts/Build-Windows.ps1; do not package stale binaries.') }
        }
    }
    if ($jpReceiptSource.Count -ne $jpActualSource.Count -or $jpReceiptArtifacts.Count -ne $ExpectedArtifacts.Count) { throw 'The build receipt does not cover the complete current source and binary set. Rebuild with scripts/Build-Windows.ps1.' }
}

if (-not [IO.Directory]::Exists($jpPackageSource)) { throw ('SourceDirectory does not exist: ' + $jpPackageSource) }
if (([IO.File]::GetAttributes($jpPackageSource) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'SourceDirectory must not be a junction or symbolic link.' }
if ($jpPackageOutput.Equals($jpPackageSource, [StringComparison]::OrdinalIgnoreCase) -or $jpPackageOutput.StartsWith($jpPackageSource + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputDirectory must be outside SourceDirectory to keep packaging outputs out of the input tree.' }
if ($jpPackageSource.StartsWith($jpPackageOutput + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputDirectory must not be an ancestor of SourceDirectory.' }
foreach ($jpRequired in @('VERSION', 'BOOT.cmd', 'LICENSE', 'NOTICE', 'COPYRIGHT', 'engine/ja21/runtime.integrity.json', 'native/refresh-manifests.ps1')) { Assert-JPPackageFile $jpRequired }
$jpPackageBinaries = @(
    'bin/JPBroker.exe', 'bin/JPConsole.exe', 'bin/JPTests.exe',
    'bin/Microsoft.Web.WebView2.Core.dll', 'bin/Microsoft.Web.WebView2.Wpf.dll', 'bin/WebView2Loader.dll'
)
foreach ($jpRequired in $jpPackageBinaries) { Assert-JPPackageFile $jpRequired }
Assert-JPBuildReceipt $jpPackageBinaries
$jpPackageRootFiles = @(
    '.gitattributes', '.gitignore', 'BOOT.cmd', 'CHANGELOG.md', 'CLEAN_MACHINE_TEST.txt',
    'CONSOLE_VERSION', 'console.json', 'CONTRIBUTING.md', 'COPYRIGHT', 'DEPENDENCIES.md',
    'GUEST-ATTESTATION.template.json', 'LICENSE', 'LICENSING.md', 'NOTICE', 'README.md',
    'SECURITY.md', 'server-config.example.json', 'STATUS.md', 'THIRD_PARTY_NOTICES.md', 'VERSION',
    'WINDOWS_PORTABILITY.md', 'RELEASES.md'
)
$jpPackageFiles = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($jpRelative in $jpPackageRootFiles + $jpPackageBinaries + @('bin/BUILD-RECEIPT.json')) { Assert-JPPackageFile $jpRelative; [void]$jpPackageFiles.Add($jpRelative) }
foreach ($jpSourceTree in @('.github', 'appliance', 'engine', 'host', 'licenses', 'native', 'photon', 'scripts', 'source')) {
    foreach ($jpRelative in @(Get-JPPackageTreeFiles $jpSourceTree)) {
        if ($jpRelative -match '(^|/)(\.git|workspace|certs|history|work|node_modules|__pycache__|\.vs|\.vscode)(/|$)' -or $jpRelative -match '\.(tmp|log|pyc|pdb|key|pfx|p12)$') { throw ('Unexpected mutable, private, or development input in an approved source tree: ' + $jpRelative + '. Review and remove it from packaging inputs first.') }
        [void]$jpPackageFiles.Add($jpRelative)
    }
}
foreach ($jpFixture in @('validation/portable-smoke.vcmd', 'validation/standalone-job.vcmd')) { Assert-JPPackageFile $jpFixture; [void]$jpPackageFiles.Add($jpFixture) }
foreach ($jpRelative in @(Get-JPPackageTreeFiles 'runtime/webview2-fixed')) { [void]$jpPackageFiles.Add($jpRelative) }
if ([IO.File]::Exists((Join-Path $jpPackageSource 'runtime/renderer.provenance.json'))) {
    Assert-JPPackageFile 'runtime/renderer.provenance.json'
    [void]$jpPackageFiles.Add('runtime/renderer.provenance.json')
}
$jpSdkVersion = '1.0.4191.47'
foreach ($jpSdkName in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll', 'WebView2Loader.dll')) {
    $jpRelative = 'runtime/webview2-sdk/' + $jpSdkVersion + '/' + $jpSdkName
    Assert-JPPackageFile $jpRelative
    [void]$jpPackageFiles.Add($jpRelative)
}
foreach ($jpRuntimeSentinel in @('msedgewebview2.exe', 'msedge.dll', 'icudtl.dat', 'resources.pak', 'EBWebView/x64/EmbeddedBrowserWebView.dll', 'locales/en-US.pak')) { Assert-JPPackageFile ('runtime/webview2-fixed/' + $jpRuntimeSentinel) }
$jpArchivePath = Join-Path $jpPackageOutput ($PackageName + '.zip')
$jpReportPath = Join-Path $jpPackageOutput ($PackageName + '.paths.json')
$jpHashPath = Join-Path $jpPackageOutput ($PackageName + '.zip.sha256')
foreach ($jpOutputPath in @($jpArchivePath, $jpReportPath, $jpHashPath)) { if ([IO.File]::Exists($jpOutputPath)) { throw ('Refusing to overwrite a prior package output: ' + $jpOutputPath) } }
[void][IO.Directory]::CreateDirectory($jpPackageOutput)
$jpStage = Join-Path $jpPackageOutput ('.stage-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
if ($jpStage.Length -gt 100) { throw 'The staging path exceeds the supported 100-unit root limit. Use a short OutputDirectory, for example D:\Codex\Releases.' }
$jpWorstCaseRoot = 'C:\' + ('r' * 97)
$jpSelectedPaths = @($jpPackageFiles) + @('SHA256SUMS.txt')
$null = & $jpPackageChecker -RelativePath $jpSelectedPaths -InstallationRoot $jpStage
$null = & $jpPackageChecker -RelativePath $jpSelectedPaths -InstallationRoot $jpWorstCaseRoot
[void][IO.Directory]::CreateDirectory($jpStage)
# Preserve staging on failure for diagnosis. Never recursively delete user paths.
foreach ($jpRelative in @($jpPackageFiles | Sort-Object)) {
    $jpSourcePath = Join-Path $jpPackageSource $jpRelative
    $jpTargetPath = Join-Path $jpStage $jpRelative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $jpTargetPath))
    [IO.File]::WriteAllBytes($jpTargetPath, [IO.File]::ReadAllBytes($jpSourcePath))
}
$jpInnerSeal = [IO.File]::ReadAllText((Join-Path $jpStage 'engine/ja21/runtime.integrity.json')) | ConvertFrom-Json
$null = & $jpPackageChecker -RelativePath @($jpInnerSeal.files | ForEach-Object { [string]$_.path }) -InstallationRoot $jpStage
foreach ($jpEntry in $jpInnerSeal.files) {
    if (-not [IO.File]::Exists((Join-Path $jpStage $jpEntry.path))) { throw ('The runtime seal references a missing staged input: ' + $jpEntry.path) }
}
& (Join-Path $jpStage 'native/refresh-manifests.ps1') | Write-Verbose
if (-not $?) { throw 'Staged integrity inventory regeneration failed.' }
$jpActualStageAudit = & $jpPackageChecker -Directory $jpStage -InstallationRoot $jpStage
# Test the complete maximum supported root budget, rather than only a short example path.
$jpWorstCaseAudit = & $jpPackageChecker -Directory $jpStage -InstallationRoot $jpWorstCaseRoot
$jpZip = [IO.Compression.ZipFile]::Open($jpArchivePath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($jpFile in Get-ChildItem -LiteralPath $jpStage -Recurse -File | Sort-Object FullName) {
        $jpRelative = $jpFile.FullName.Substring($jpStage.Length + 1).Replace('\', '/')
        $jpEntry = $jpZip.CreateEntry($jpRelative, [IO.Compression.CompressionLevel]::Optimal)
        $jpEntry.LastWriteTime = [DateTimeOffset]::Parse('2026-01-01T00:00:00+00:00', [Globalization.CultureInfo]::InvariantCulture)
        $jpInput = [IO.File]::OpenRead($jpFile.FullName)
        $jpOutput = $jpEntry.Open()
        try { $jpInput.CopyTo($jpOutput) } finally { $jpOutput.Dispose(); $jpInput.Dispose() }
    }
}
finally { $jpZip.Dispose() }
$jpArchiveAudit = & $jpPackageChecker -Archive $jpArchivePath -InstallationRoot $jpWorstCaseRoot
if ($jpArchiveAudit.files -ne $jpActualStageAudit.files) { throw 'The archive file count differs from the audited staging directory.' }
$jpPackageDigest = Get-JPPackageDigest $jpArchivePath
$jpPackageReport = [pscustomobject][ordered]@{
    format = 'lkvs-windows-package/1'
    version = [IO.File]::ReadAllText((Join-Path $jpStage 'VERSION')).Trim()
    qualification = 'Unqualified source candidate. Packaging and path audits do not establish a clean-machine test pass.'
    archive = $jpArchivePath
    archive_bytes = ([IO.FileInfo]$jpArchivePath).Length
    archive_sha256 = $jpPackageDigest
    staging_directory = $jpStage
    layout = 'BOOT.cmd is at the archive root; extract the archive contents into C:\LKVS or D:\LKVS.'
    stage_path_audit = $jpActualStageAudit
    maximum_root_path_audit = $jpWorstCaseAudit
    archive_path_audit = $jpArchiveAudit
    exclusions = @('workspace/', 'certs/', 'history/', '.git/', 'private validation evidence', 'developer caches', 'prior archives')
}
[IO.File]::WriteAllText($jpReportPath, ($jpPackageReport | ConvertTo-Json -Depth 10) + "`n", $jpPackageUtf8)
[IO.File]::WriteAllText($jpHashPath, $jpPackageDigest + '  ' + [IO.Path]::GetFileName($jpArchivePath) + "`n", $jpPackageUtf8)
$jpPackageReport
