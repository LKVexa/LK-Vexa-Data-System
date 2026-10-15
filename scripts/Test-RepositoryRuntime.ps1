# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 RUSSELL PHILIP SMITHSON
[CmdletBinding()]
param(
    [string]$SourceDirectory = (Split-Path -Parent $PSScriptRoot),
    [string]$EvidenceDirectory = '',
    [switch]$SelfTest,
    [switch]$RequireReparseTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$jpTestSource = [IO.Path]::GetFullPath($SourceDirectory)
if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) { $EvidenceDirectory = Join-Path $jpTestSource 'work/rr' }
$jpTestEvidence = [IO.Path]::GetFullPath($EvidenceDirectory)
$jpTestRun = Join-Path $jpTestEvidence ('run-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
if ($jpTestRun.Length -gt 82) { throw 'Use a shorter EvidenceDirectory so the synthetic installation roots remain below100 UTF-16 units.' }
[void][IO.Directory]::CreateDirectory($jpTestRun)
$jpTestUtf8 = New-Object Text.UTF8Encoding($false)
$jpTestHelperPath = Join-Path $jpTestSource 'scripts/Restore-RepositoryRuntime.ps1'
$jpTestHelper = [scriptblock]::Create([IO.File]::ReadAllText($jpTestHelperPath))
$jpTestResults = New-Object 'Collections.Generic.List[object]'

function Get-JPTestDigest([byte[]]$Bytes) {
    $jpTestHasher = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($jpTestHasher.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $jpTestHasher.Dispose() }
}

function Write-JPTestManifest($Fixture) {
    [IO.File]::WriteAllText((Join-Path $Fixture.root 'scripts/Repository-Runtime.json'), (($Fixture.spec | ConvertTo-Json -Depth 8) + "`n"), $jpTestUtf8)
}

function New-JPTestFixture([string]$Name) {
    $jpFixtureRoot = Join-Path $jpTestRun $Name
    foreach ($jpFixtureDirectory in @('scripts', 'payload/webview2')) { [void][IO.Directory]::CreateDirectory((Join-Path $jpFixtureRoot $jpFixtureDirectory)) }
    $jpFixtureBytes = [byte[]](0..70)
    $jpFixtureParts = @()
    for ($jpFixtureIndex = 0; $jpFixtureIndex -lt 3; $jpFixtureIndex++) {
        $jpFixtureOffset = $jpFixtureIndex * 32
        $jpFixtureLength = [Math]::Min(32, $jpFixtureBytes.Length - $jpFixtureOffset)
        $jpFixturePartBytes = New-Object byte[] $jpFixtureLength
        [Array]::Copy($jpFixtureBytes, $jpFixtureOffset, $jpFixturePartBytes, 0, $jpFixtureLength)
        $jpFixtureRelative = 'payload/webview2/msedge.dll.' + $jpFixtureIndex.ToString('D3', [Globalization.CultureInfo]::InvariantCulture)
        [IO.File]::WriteAllBytes((Join-Path $jpFixtureRoot $jpFixtureRelative), $jpFixturePartBytes)
        $jpFixtureParts += [ordered]@{ path = $jpFixtureRelative; bytes = $jpFixtureLength; sha256 = Get-JPTestDigest $jpFixturePartBytes }
    }
    $jpFixture = [pscustomobject]@{
        root = $jpFixtureRoot
        data = $jpFixtureBytes
        spec = [ordered]@{ format = 'lkvs-repository-runtime/1'; target = 'runtime/webview2-fixed/msedge.dll'; bytes = $jpFixtureBytes.Length; sha256 = Get-JPTestDigest $jpFixtureBytes; parts = $jpFixtureParts }
    }
    Write-JPTestManifest $jpFixture
    return $jpFixture
}

function Assert-JPTestRejected($Fixture) {
    $jpRejected = $false
    try { & $jpTestHelper -SourceDirectory $Fixture.root | Out-Null }
    catch { $jpRejected = $true }
    if (-not $jpRejected) { throw 'The invalid runtime fixture was accepted.' }
    if ([IO.File]::Exists((Join-Path $Fixture.root 'runtime/webview2-fixed/msedge.dll'))) { throw 'A failed restoration published the DLL.' }
    $jpFixtureTemp = Join-Path $Fixture.root 'workspace/temp'
    if ([IO.Directory]::Exists($jpFixtureTemp) -and @([IO.Directory]::EnumerateFiles($jpFixtureTemp, '.restore-*.tmp')).Count -ne 0) { throw 'A failed restoration left its temporary file behind.' }
}

function Invoke-JPTestCase([string]$Name, [scriptblock]$Case) {
    try {
        & $Case
        [void]$jpTestResults.Add([pscustomobject]@{ name = $Name; passed = $true; skipped = $false })
        Write-Output ('PASS ' + $Name)
    }
    catch [PlatformNotSupportedException] {
        [void]$jpTestResults.Add([pscustomobject]@{ name = $Name; passed = $true; skipped = $true; reason = $_.Exception.Message })
        Write-Output ('SKIP ' + $Name + ': ' + $_.Exception.Message)
    }
    catch {
        [void]$jpTestResults.Add([pscustomobject]@{ name = $Name; passed = $false; skipped = $false; error = $_.Exception.Message })
        Write-Output ('FAIL ' + $Name + ': ' + $_.Exception.Message)
    }
}

Invoke-JPTestCase 'verified exact reconstruction' {
    $jpFixture = New-JPTestFixture 'ok'
    & $jpTestHelper -SourceDirectory $jpFixture.root | Out-Null
    $jpActualBytes = [IO.File]::ReadAllBytes((Join-Path $jpFixture.root 'runtime/webview2-fixed/msedge.dll'))
    if ($jpActualBytes.Length -ne $jpFixture.data.Length -or (Get-JPTestDigest $jpActualBytes) -cne $jpFixture.spec.sha256) { throw 'The reconstructed bytes differ from the source.' }
    foreach ($jpPart in $jpFixture.spec.parts) { if (-not [IO.File]::Exists((Join-Path $jpFixture.root $jpPart.path))) { throw 'A source part was deleted.' } }
}
Invoke-JPTestCase 'corrupt part' {
    $jpFixture = New-JPTestFixture 'badpart'
    $jpPath = Join-Path $jpFixture.root $jpFixture.spec.parts[0].path
    $jpBytes = [IO.File]::ReadAllBytes($jpPath)
    $jpBytes[0] = $jpBytes[0] -bxor 1
    [IO.File]::WriteAllBytes($jpPath, $jpBytes)
    Assert-JPTestRejected $jpFixture
}
Invoke-JPTestCase 'missing part' {
    $jpFixture = New-JPTestFixture 'missing'
    [IO.File]::Delete((Join-Path $jpFixture.root $jpFixture.spec.parts[1].path))
    Assert-JPTestRejected $jpFixture
}
Invoke-JPTestCase 'truncated part' {
    $jpFixture = New-JPTestFixture 'truncated'
    [IO.File]::WriteAllBytes((Join-Path $jpFixture.root $jpFixture.spec.parts[1].path), [byte[]](1, 2, 3))
    Assert-JPTestRejected $jpFixture
}
Invoke-JPTestCase 'complete hash mismatch' {
    $jpFixture = New-JPTestFixture 'wholehash'
    $jpFixture.spec.sha256 = '0' * 64
    Write-JPTestManifest $jpFixture
    Assert-JPTestRejected $jpFixture
}
Invoke-JPTestCase 'manifest byte-count mismatch' {
    $jpFixture = New-JPTestFixture 'total'
    $jpFixture.spec.bytes += 1
    Write-JPTestManifest $jpFixture
    Assert-JPTestRejected $jpFixture
}
Invoke-JPTestCase 'target traversal' {
    $jpFixture = New-JPTestFixture 'target'
    $jpFixture.spec.target = '../escape.dll'
    Write-JPTestManifest $jpFixture
    Assert-JPTestRejected $jpFixture
}
Invoke-JPTestCase 'part traversal' {
    $jpFixture = New-JPTestFixture 'path'
    $jpFixture.spec.parts[0].path = '../escape.dll'
    Write-JPTestManifest $jpFixture
    Assert-JPTestRejected $jpFixture
}
Invoke-JPTestCase 'parts out of order' {
    $jpFixture = New-JPTestFixture 'order'
    $jpFixture.spec.parts = @($jpFixture.spec.parts[1], $jpFixture.spec.parts[0], $jpFixture.spec.parts[2])
    Write-JPTestManifest $jpFixture
    Assert-JPTestRejected $jpFixture
}
Invoke-JPTestCase 'part exceeds 48 MiB' {
    $jpFixture = New-JPTestFixture 'oversize'
    $jpFixture.spec.parts[0].bytes = 48 * 1024 * 1024 + 1
    Write-JPTestManifest $jpFixture
    Assert-JPTestRejected $jpFixture
}
Invoke-JPTestCase 'unknown manifest field' {
    $jpFixture = New-JPTestFixture 'field'
    $jpFixture.spec['unexpected'] = $true
    Write-JPTestManifest $jpFixture
    Assert-JPTestRejected $jpFixture
}
Invoke-JPTestCase 'existing file never overwritten' {
    $jpFixture = New-JPTestFixture 'existing'
    $jpTarget = Join-Path $jpFixture.root 'runtime/webview2-fixed/msedge.dll'
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($jpTarget))
    $jpOriginal = [byte[]](9, 8, 7, 6)
    [IO.File]::WriteAllBytes($jpTarget, $jpOriginal)
    [IO.File]::Delete((Join-Path $jpFixture.root 'scripts/Repository-Runtime.json'))
    & $jpTestHelper -SourceDirectory $jpFixture.root | Out-Null
    if ((Get-JPTestDigest ([IO.File]::ReadAllBytes($jpTarget))) -cne (Get-JPTestDigest $jpOriginal)) { throw 'An existing DLL was replaced.' }
}
Invoke-JPTestCase 'overlong installation root' {
    $jpFixture = New-JPTestFixture ('r' * [Math]::Max(50, 101 - $jpTestRun.Length - 1))
    if ($jpFixture.root.Length -le 100) { throw 'The overlong-root fixture is not long enough.' }
    Assert-JPTestRejected $jpFixture
}
Invoke-JPTestCase 'junction payload directory' {
    $jpFixture = New-JPTestFixture 'junction'
    $jpLinkPath = [IO.Path]::GetFullPath((Join-Path $jpFixture.root 'payload/webview2'))
    $jpMovedPath = [IO.Path]::GetFullPath((Join-Path $jpFixture.root 'stored-parts'))
    foreach ($jpBoundedPath in @($jpLinkPath, $jpMovedPath)) { if (-not $jpBoundedPath.StartsWith($jpFixture.root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The junction fixture move escaped its own root.' } }
    [IO.Directory]::Move($jpLinkPath, $jpMovedPath)
    try { $null = New-Item -ItemType Junction -Path $jpLinkPath -Target $jpMovedPath }
    catch {
        if ($RequireReparseTest) { throw }
        throw [PlatformNotSupportedException]::new('This environment denied junction creation: ' + $_.Exception.Message)
    }
    Assert-JPTestRejected $jpFixture
}

$jpTestFailed = @($jpTestResults | Where-Object { -not $_.passed })
$jpTestSkipped = @($jpTestResults | Where-Object { $_.skipped })
$jpTestReport = [pscustomobject][ordered]@{
    format = 'lkvs-repository-runtime-tests/1'
    powershell = $PSVersionTable.PSVersion.ToString()
    passed = $jpTestFailed.Count -eq 0
    skipped = $jpTestSkipped.Count
    cases = $jpTestResults.ToArray()
    evidence_directory = $jpTestRun
}
[IO.File]::WriteAllText((Join-Path $jpTestRun 'result.json'), (($jpTestReport | ConvertTo-Json -Depth 8) + "`n"), $jpTestUtf8)
if ($jpTestFailed.Count -ne 0) { throw ($jpTestFailed.Count.ToString() + ' runtime restoration fixture(s) failed. Evidence: ' + $jpTestRun) }
Write-Output (($jpTestResults.Count - $jpTestSkipped.Count).ToString() + ' runtime restoration fixtures passed; ' + $jpTestSkipped.Count + ' skipped. Evidence: ' + $jpTestRun)
