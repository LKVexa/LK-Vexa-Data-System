# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 RUSSELL PHILIP SMITHSON
[CmdletBinding()]
param([string]$SourceDirectory = (Split-Path -Parent $PSScriptRoot))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or $PSVersionTable.PSVersion -lt [version]'5.1') { throw 'Runtime preparation requires Windows PowerShell 5.1 or later.' }
if ($SourceDirectory -notmatch '^[A-Za-z]:[\\/]') { throw 'Use an absolute local drive path for SourceDirectory.' }
foreach ($jpRootComponent in $SourceDirectory.Substring(3).Replace('/', '\').Split('\')) {
    if ($jpRootComponent.Length -gt 0 -and ($jpRootComponent -in @('.', '..') -or $jpRootComponent.EndsWith('.') -or $jpRootComponent.EndsWith(' ') -or $jpRootComponent.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0)) { throw 'SourceDirectory must not contain Windows path aliases.' }
}
$jpRestoreRoot = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\')
if ($jpRestoreRoot.Length -eq 2) { $jpRestoreRoot += '\' }
if ($jpRestoreRoot.TrimEnd('\').Length -gt 100) { throw 'Extract the complete repository into a short local folder such as C:\LKVS; its root must fit 100 UTF-16 code units.' }
if (-not [IO.Directory]::Exists($jpRestoreRoot)) { throw 'SourceDirectory does not exist.' }
$jpRestoreDrive = New-Object IO.DriveInfo([IO.Path]::GetPathRoot($jpRestoreRoot))
if ($jpRestoreDrive.DriveType -in @([IO.DriveType]::Network, [IO.DriveType]::Unknown, [IO.DriveType]::NoRootDirectory)) { throw 'Extract the repository to a writable local or removable drive, not a mapped network drive.' }
$jpRestoreTargetRelative = 'runtime/webview2-fixed/msedge.dll'
$jpRestoreTarget = Join-Path $jpRestoreRoot $jpRestoreTargetRelative
$jpRestoreManifest = Join-Path $jpRestoreRoot 'scripts/Repository-Runtime.json'
$jpRestoreMaximumPartBytes = 48L * 1024 * 1024

function Assert-JPRestorePlainPath([string]$Path, [bool]$AllowMissing = $false) {
    if ($Path.Length -gt 259 -or $Path -notmatch '^[A-Za-z]:\\') { throw 'Runtime preparation path exceeds the local Windows path budget.' }
    $jpProbe = [IO.Path]::GetPathRoot($Path)
    foreach ($jpComponent in $Path.Substring($jpProbe.Length).Split('\')) {
        if ($jpComponent.Length -eq 0) { continue }
        if ($jpComponent -in @('.', '..') -or $jpComponent.EndsWith('.') -or $jpComponent.EndsWith(' ') -or $jpComponent.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Runtime preparation refuses aliased Windows paths.' }
        $jpProbe = Join-Path $jpProbe $jpComponent
        try { $jpAttributes = [IO.File]::GetAttributes($jpProbe) }
        catch [IO.FileNotFoundException] { if ($AllowMissing) { continue }; throw }
        catch [IO.DirectoryNotFoundException] { if ($AllowMissing) { continue }; throw }
        if (($jpAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Runtime preparation refuses a reparse point: ' + $jpProbe) }
        if (($jpAttributes -band [IO.FileAttributes]::Directory) -ne 0 -and $jpProbe.Length -gt 247) { throw 'Runtime preparation directory exceeds 247 UTF-16 code units.' }
    }
}

function Assert-JPRestoreFields($Value, [string[]]$Names) {
    if ($null -eq $Value -or $Value -isnot [pscustomobject]) { throw 'The runtime manifest requires JSON objects.' }
    $jpActualNames = @($Value.PSObject.Properties.Name)
    if ($jpActualNames.Count -ne $Names.Count -or @(Compare-Object $jpActualNames $Names -CaseSensitive).Count -ne 0) { throw 'The runtime manifest has missing or unexpected fields.' }
}

function Assert-JPRestoreSize($Value, [long]$Maximum) {
    if (($Value -isnot [int] -and $Value -isnot [long]) -or $Value -lt 1 -or $Value -gt $Maximum) { throw 'The runtime manifest contains an invalid byte count.' }
}

function Assert-JPRestoreHash($Value) {
    if ($Value -isnot [string] -or $Value -cnotmatch '^[0-9a-f]{64}$') { throw 'The runtime manifest requires lowercase SHA-256 values.' }
}

function Get-JPRestoreHashText([byte[]]$Value) { ([BitConverter]::ToString($Value)).Replace('-', '').ToLowerInvariant() }

Assert-JPRestorePlainPath $jpRestoreRoot
Assert-JPRestorePlainPath $jpRestoreTarget $true
if ([IO.Directory]::Exists($jpRestoreTarget)) { throw 'The bundled browser DLL path is occupied by a directory.' }
if ([IO.File]::Exists($jpRestoreTarget)) {
    # Existing files are never replaced or silently repaired. The normal native
    # package verifier checks their bytes before starting the browser.
    Write-Verbose 'The bundled browser DLL already exists; native verification will check it.'
    return
}
Assert-JPRestorePlainPath $jpRestoreManifest
if (([IO.FileInfo]$jpRestoreManifest).Length -gt 65536) { throw 'The runtime manifest exceeds its size limit.' }
$jpRestoreUtf8 = New-Object Text.UTF8Encoding($false, $true)
$jpRestoreSpec = [IO.File]::ReadAllText($jpRestoreManifest, $jpRestoreUtf8) | ConvertFrom-Json
Assert-JPRestoreFields $jpRestoreSpec @('format', 'target', 'bytes', 'sha256', 'parts')
if ($jpRestoreSpec.format -cne 'lkvs-repository-runtime/1' -or $jpRestoreSpec.target -cne $jpRestoreTargetRelative) { throw 'The runtime manifest format or fixed DLL target is unsupported.' }
Assert-JPRestoreSize $jpRestoreSpec.bytes (512L * 1024 * 1024)
Assert-JPRestoreHash $jpRestoreSpec.sha256
$jpRestoreParts = @($jpRestoreSpec.parts)
if ($jpRestoreParts.Count -lt 1 -or $jpRestoreParts.Count -gt 16) { throw 'The runtime manifest must contain 1 to 16 ordered parts.' }
$jpRestoreTotalBytes = 0L
for ($jpPartIndex = 0; $jpPartIndex -lt $jpRestoreParts.Count; $jpPartIndex++) {
    $jpPart = $jpRestoreParts[$jpPartIndex]
    Assert-JPRestoreFields $jpPart @('path', 'bytes', 'sha256')
    $jpExpectedPart = 'payload/webview2/msedge.dll.' + $jpPartIndex.ToString('D3', [Globalization.CultureInfo]::InvariantCulture)
    if ($jpPart.path -isnot [string] -or $jpPart.path -cne $jpExpectedPart) { throw 'Runtime parts must use the exact consecutive payload/webview2/msedge.dll.NNN paths.' }
    Assert-JPRestoreSize $jpPart.bytes $jpRestoreMaximumPartBytes
    Assert-JPRestoreHash $jpPart.sha256
    $jpRestoreTotalBytes += [long]$jpPart.bytes
    $jpPartPath = Join-Path $jpRestoreRoot $jpPart.path
    Assert-JPRestorePlainPath $jpPartPath
    if ([IO.Directory]::Exists($jpPartPath) -or ([IO.FileInfo]$jpPartPath).Length -ne [long]$jpPart.bytes) { throw ('Runtime part is truncated or has an unexpected size: ' + $jpPart.path) }
}
if ($jpRestoreTotalBytes -ne [long]$jpRestoreSpec.bytes) { throw 'Runtime part byte counts do not equal the complete DLL size.' }

$jpRestoreTempDirectory = Join-Path $jpRestoreRoot 'workspace/temp'
Assert-JPRestorePlainPath $jpRestoreTempDirectory $true
[void][IO.Directory]::CreateDirectory($jpRestoreTempDirectory)
Assert-JPRestorePlainPath $jpRestoreTempDirectory
$jpRestoreTemporary = Join-Path $jpRestoreTempDirectory ('.restore-' + [Guid]::NewGuid().ToString('N') + '.tmp')
Assert-JPRestorePlainPath $jpRestoreTemporary $true
$jpRestoreOutput = $null
$jpRestoreWholeHash = $null
$jpRestoreCreated = $false
try {
    Write-Output 'Preparing the bundled browser offline. This is needed only once after repository extraction.'
    $jpRestoreOutput = New-Object IO.FileStream($jpRestoreTemporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $jpRestoreCreated = $true
    $jpRestoreWholeHash = [Security.Cryptography.SHA256]::Create()
    $jpRestoreBuffer = New-Object byte[] (1024 * 1024)
    foreach ($jpPart in $jpRestoreParts) {
        $jpPartPath = Join-Path $jpRestoreRoot $jpPart.path
        Assert-JPRestorePlainPath $jpPartPath
        $jpPartInput = $null
        $jpPartHash = $null
        try {
            $jpPartInput = New-Object IO.FileStream($jpPartPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            if ($jpPartInput.Length -ne [long]$jpPart.bytes) { throw ('Runtime part changed size: ' + $jpPart.path) }
            $jpPartHash = [Security.Cryptography.SHA256]::Create()
            $jpPartRead = 0L
            while (($jpRead = $jpPartInput.Read($jpRestoreBuffer, 0, $jpRestoreBuffer.Length)) -gt 0) {
                $jpRestoreOutput.Write($jpRestoreBuffer, 0, $jpRead)
                [void]$jpPartHash.TransformBlock($jpRestoreBuffer, 0, $jpRead, $jpRestoreBuffer, 0)
                [void]$jpRestoreWholeHash.TransformBlock($jpRestoreBuffer, 0, $jpRead, $jpRestoreBuffer, 0)
                $jpPartRead += $jpRead
            }
            [void]$jpPartHash.TransformFinalBlock((New-Object byte[] 0), 0, 0)
            if ($jpPartRead -ne [long]$jpPart.bytes -or (Get-JPRestoreHashText $jpPartHash.Hash) -cne $jpPart.sha256) { throw ('Runtime part SHA-256 verification failed: ' + $jpPart.path) }
        }
        finally {
            if ($null -ne $jpPartInput) { $jpPartInput.Dispose() }
            if ($null -ne $jpPartHash) { $jpPartHash.Dispose() }
        }
    }
    [void]$jpRestoreWholeHash.TransformFinalBlock((New-Object byte[] 0), 0, 0)
    if ($jpRestoreOutput.Length -ne [long]$jpRestoreSpec.bytes -or (Get-JPRestoreHashText $jpRestoreWholeHash.Hash) -cne $jpRestoreSpec.sha256) { throw 'Complete browser DLL SHA-256 verification failed.' }
    $jpRestoreOutput.Flush($true)
    $jpRestoreOutput.Dispose()
    $jpRestoreOutput = $null
    $jpRestoreParent = [IO.Path]::GetDirectoryName($jpRestoreTarget)
    Assert-JPRestorePlainPath $jpRestoreParent $true
    [void][IO.Directory]::CreateDirectory($jpRestoreParent)
    Assert-JPRestorePlainPath $jpRestoreTarget $true
    # Both paths are below one local drive with no reparse ancestors. File.Move
    # is an atomic same-volume rename and fails if another file already exists.
    [IO.File]::Move($jpRestoreTemporary, $jpRestoreTarget)
    $jpRestoreCreated = $false
    Write-Output 'Bundled browser restored. Starting the normal package verification.'
}
finally {
    if ($null -ne $jpRestoreOutput) { $jpRestoreOutput.Dispose() }
    if ($null -ne $jpRestoreWholeHash) { $jpRestoreWholeHash.Dispose() }
    if ($jpRestoreCreated -and [IO.File]::Exists($jpRestoreTemporary)) { [IO.File]::Delete($jpRestoreTemporary) }
}
