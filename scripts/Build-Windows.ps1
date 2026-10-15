# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 RUSSELL PHILIP SMITHSON
[CmdletBinding()]
param([string]$GoExe = 'go')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$jpBuildRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ($jpBuildRoot.TrimEnd('\', '/').Length -gt 100 -or $jpBuildRoot -notmatch '^[A-Za-z]:\\') {
    throw 'Use a local source folder with a root of at most 100 UTF-16 code units, such as C:\LKVS. This build does not require enabling Windows long paths.'
}
$jpNativeDirectory = Join-Path $jpBuildRoot 'native'
$jpBinDirectory = Join-Path $jpBuildRoot 'bin'
$jpBuildDirectory = Join-Path $jpBuildRoot 'workspace\build'
$jpSdkVersion = '1.0.4191.47'
$jpRendererVersion = '155.0.4283.45'
$jpSdkDirectory = Join-Path $jpBuildRoot ('runtime\webview2-sdk\' + $jpSdkVersion)
$jpRendererDirectory = Join-Path $jpBuildRoot 'runtime\webview2-fixed'

function Assert-JPBuildFile([string]$Path, [string]$Description) {
    if (-not [IO.File]::Exists($Path)) { throw ($Description + ' is missing: ' + $Path) }
}
function Assert-JPX64Image([string]$Path) {
    $jpImageStream = [IO.File]::OpenRead($Path)
    try {
        $jpDosHeader = New-Object byte[] 64
        if ($jpImageStream.Read($jpDosHeader, 0, 64) -ne 64 -or [BitConverter]::ToUInt16($jpDosHeader, 0) -ne 0x5A4D) {
            throw ('Invalid executable DOS header: ' + $Path)
        }
        $jpPeOffset = [BitConverter]::ToInt32($jpDosHeader, 60)
        if ($jpPeOffset -lt 64 -or $jpPeOffset -gt ($jpImageStream.Length - 6)) {
            throw ('Invalid executable PE offset: ' + $Path)
        }
        $jpImageStream.Position = $jpPeOffset
        $jpPeHeader = New-Object byte[] 6
        if ($jpImageStream.Read($jpPeHeader, 0, 6) -ne 6 -or [BitConverter]::ToUInt32($jpPeHeader, 0) -ne 0x00004550) {
            throw ('Invalid executable PE header: ' + $Path)
        }
        if ([BitConverter]::ToUInt16($jpPeHeader, 4) -ne 0x8664) { throw ('A native Windows x64 component is required: ' + $Path) }
    }
    finally { $jpImageStream.Dispose() }
}
function Get-JPBuildFileRecord([string]$Path) {
    $jpRecordBytes = [IO.File]::ReadAllBytes($Path)
    $jpHasher = [Security.Cryptography.SHA256]::Create()
    try { $jpRecordHash = ([BitConverter]::ToString($jpHasher.ComputeHash($jpRecordBytes))).Replace('-', '').ToLowerInvariant() }
    finally { $jpHasher.Dispose() }
    [pscustomobject][ordered]@{ path = $Path.Substring($jpBuildRoot.Length + 1).Replace('\', '/'); bytes = $jpRecordBytes.LongLength; sha256 = $jpRecordHash }
}
function Get-JPBuildSourceRecords {
    $jpCompiledSources = @([IO.Directory]::GetFiles((Join-Path $jpBuildRoot 'host'), '*.cs', [IO.SearchOption]::AllDirectories)) +
        @([IO.Directory]::GetFiles($jpNativeDirectory, '*.go', [IO.SearchOption]::AllDirectories)) +
        @(Join-Path $jpNativeDirectory 'go.mod')
    $jpCompiledSources | Sort-Object | ForEach-Object { Get-JPBuildFileRecord $_ }
}
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem -or -not [Environment]::Is64BitProcess) {
    throw 'Run this script in 64-bit PowerShell on Windows x64. The application target is Windows 11 x64.'
}
$jpGoCommand = Get-Command -Name $GoExe -CommandType Application -ErrorAction Stop | Select-Object -First 1
$jpGoPath = $jpGoCommand.Source
$jpFrameworkDirectory = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319'
$jpCompilerPath = Join-Path $jpFrameworkDirectory 'csc.exe'
Assert-JPBuildFile $jpCompilerPath 'The Windows .NET Framework x64 C# compiler'
$jpFrameworkReferences = @(
    'System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Xaml.dll',
    'System.Net.Http.dll', 'WPF\PresentationFramework.dll', 'WPF\PresentationCore.dll', 'WPF\WindowsBase.dll'
)
$jpCompilerReferences = @()
foreach ($jpReference in $jpFrameworkReferences) {
    $jpReferencePath = Join-Path $jpFrameworkDirectory $jpReference
    Assert-JPBuildFile $jpReferencePath 'A required .NET Framework/WPF reference assembly'
    $jpCompilerReferences += '/reference:' + $jpReferencePath
}
$jpSdkFiles = @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll', 'WebView2Loader.dll')
foreach ($jpSdkFile in $jpSdkFiles) {
    Assert-JPBuildFile (Join-Path $jpSdkDirectory $jpSdkFile) ('The manually supplied WebView2 SDK ' + $jpSdkVersion + ' component')
}
Assert-JPX64Image (Join-Path $jpSdkDirectory 'WebView2Loader.dll')
foreach ($jpSdkReference in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll')) {
    $jpSdkReferencePath = Join-Path $jpSdkDirectory $jpSdkReference
    $jpSdkFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($jpSdkReferencePath).FileVersion
    if ($jpSdkFileVersion -ne $jpSdkVersion) {
        throw ('Expected WebView2 SDK ' + $jpSdkVersion + ', found ' + $jpSdkFileVersion + ': ' + $jpSdkReferencePath)
    }
    $jpCompilerReferences += '/reference:' + $jpSdkReferencePath
}
# Sentinels do not prove completeness: retain every official runtime file.
foreach ($jpRendererFile in @('msedgewebview2.exe', 'msedge.dll', 'icudtl.dat', 'resources.pak', 'EBWebView\x64\EmbeddedBrowserWebView.dll')) {
    Assert-JPBuildFile (Join-Path $jpRendererDirectory $jpRendererFile) 'A manually supplied Fixed Version WebView2 runtime component'
}
if (-not [IO.Directory]::Exists((Join-Path $jpRendererDirectory 'locales'))) {
    throw ('The complete Fixed Version WebView2 runtime locales directory is missing: ' + $jpRendererDirectory)
}
$jpRendererFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $jpRendererDirectory 'msedgewebview2.exe')).FileVersion
Assert-JPX64Image (Join-Path $jpRendererDirectory 'msedgewebview2.exe')
if ($jpRendererFileVersion -ne $jpRendererVersion) {
    throw ('Expected Fixed Version WebView2 runtime ' + $jpRendererVersion + ', found ' + $jpRendererFileVersion)
}
$jpHostSourceNames = @(
    'JA21VirtualBrowser.cs', 'Vec1Substrate.cs', 'PortableDesktop.cs',
    'LiveWebContracts.cs', 'LiveWebBridge.cs', 'JPConsoleEntry.cs'
)
$jpHostSources = @()
foreach ($jpHostSourceName in $jpHostSourceNames) {
    $jpHostSourcePath = Join-Path $jpBuildRoot ('host\' + $jpHostSourceName)
    Assert-JPBuildFile $jpHostSourcePath 'A presenter source file'
    $jpHostSources += $jpHostSourcePath
}
foreach ($jpBuildInput in @('native\go.mod', 'native\refresh-manifests.ps1', 'engine\ja21\runtime.integrity.json', 'source\jp250-service.ja', 'appliance\web\index.html')) {
    Assert-JPBuildFile (Join-Path $jpBuildRoot $jpBuildInput) 'A required source/build input'
}
$jpEnvironmentNames = @(
    'GOROOT', 'GOENV', 'GOWORK', 'GOTOOLCHAIN', 'GOPROXY', 'GOSUMDB',
    'GOFLAGS', 'GOOS', 'GOARCH', 'CGO_ENABLED', 'GOPATH', 'GOMODCACHE', 'GOCACHE', 'TEMP', 'TMP'
)
$jpEnvironmentBackup = @{}
foreach ($jpEnvironmentName in $jpEnvironmentNames) {
    $jpEnvironmentBackup[$jpEnvironmentName] = [Environment]::GetEnvironmentVariable($jpEnvironmentName, 'Process')
}
$jpLocationPushed = $false
try {
    [void][IO.Directory]::CreateDirectory($jpBinDirectory)
    foreach ($jpBuildSubdirectory in @('tmp', 'go-cache', 'gopath', 'go-modules')) {
        [void][IO.Directory]::CreateDirectory((Join-Path $jpBuildDirectory $jpBuildSubdirectory))
    }
    # Configure this invocation only; every value is restored in finally.
    [Environment]::SetEnvironmentVariable('GOROOT', $null, 'Process')
    $env:GOENV = 'off'
    $env:GOWORK = 'off'
    $env:GOTOOLCHAIN = 'local'
    $env:GOPROXY = 'off'
    $env:GOSUMDB = 'off'
    $env:GOFLAGS = ''
    $env:GOOS = 'windows'
    $env:GOARCH = 'amd64'
    $env:CGO_ENABLED = '0'
    $env:GOPATH = Join-Path $jpBuildDirectory 'gopath'
    $env:GOMODCACHE = Join-Path $jpBuildDirectory 'go-modules'
    $env:GOCACHE = Join-Path $jpBuildDirectory 'go-cache'
    $env:TEMP = Join-Path $jpBuildDirectory 'tmp'
    $env:TMP = $env:TEMP
    Push-Location -LiteralPath $jpNativeDirectory
    $jpLocationPushed = $true
    $jpGoVersionOutput = (& $jpGoPath version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'The selected Go executable could not report its version.' }
    if ($jpGoVersionOutput -notmatch '\bgo(\d+)\.(\d+)(?:\.(\d+))?\b') {
        throw ('Unrecognized Go version output: ' + $jpGoVersionOutput)
    }
    $jpGoMajor = [int]$Matches[1]
    $jpGoMinor = [int]$Matches[2]
    if ($jpGoMajor -lt 1 -or ($jpGoMajor -eq 1 -and $jpGoMinor -lt 27)) {
        throw ('Go 1.27.0 or later is required; reference toolchain: Go 1.27.2. Found: ' + $jpGoVersionOutput)
    }
    if ($jpGoVersionOutput -notmatch '\bgo1\.27\.2\s') {
        Write-Warning ('Reference builds use Go 1.27.2. This toolchain is unqualified for the snapshot: ' + $jpGoVersionOutput)
    }
    Write-Output ('Selected toolchain: ' + $jpGoVersionOutput)
    $jpSourceRecordsBefore = @(Get-JPBuildSourceRecords)
    $jpProductVersionBefore = [IO.File]::ReadAllText((Join-Path $jpBuildRoot 'VERSION')).Trim()
    & $jpGoPath build '-work' '-buildvcs=false' '-trimpath' '-ldflags=-s -w' '-o' (Join-Path $jpBinDirectory 'JPBroker.exe') '.'
    if ($LASTEXITCODE -ne 0) { throw 'Native broker compilation failed.' }
    & $jpGoPath test '-c' '-work' '-buildvcs=false' '-trimpath' '-o' (Join-Path $jpBinDirectory 'JPTests.exe') '.'
    if ($LASTEXITCODE -ne 0) { throw 'Native regression executable compilation failed.' }
    & $jpCompilerPath '/nologo' '/target:exe' '/platform:x64' '/optimize+' '/main:VBJA21.JPConsoleEntry' ('/out:' + (Join-Path $jpBinDirectory 'JPConsole.exe')) @jpCompilerReferences @jpHostSources
    if ($LASTEXITCODE -ne 0) { throw 'Native WPF presenter compilation failed.' }
    foreach ($jpSdkFile in $jpSdkFiles) {
        $jpSdkBytes = [IO.File]::ReadAllBytes((Join-Path $jpSdkDirectory $jpSdkFile))
        [IO.File]::WriteAllBytes((Join-Path $jpBinDirectory $jpSdkFile), $jpSdkBytes)
    }
    # Bind the binaries to the complete compiled source set before packaging.
    # Write this before inventory regeneration so the receipt is sealed too.
    $jpSourceRecords = @(Get-JPBuildSourceRecords)
    if ($jpSourceRecords.Count -ne $jpSourceRecordsBefore.Count -or
        @(Compare-Object -ReferenceObject $jpSourceRecordsBefore -DifferenceObject $jpSourceRecords -Property path, bytes, sha256 -CaseSensitive).Count -ne 0 -or
        [IO.File]::ReadAllText((Join-Path $jpBuildRoot 'VERSION')).Trim() -ne $jpProductVersionBefore) {
        throw 'Source files or VERSION changed during compilation. No build receipt was issued. Rebuild from an unchanged source folder.'
    }
    $jpArtifactRecords = @(@('JPBroker.exe', 'JPTests.exe', 'JPConsole.exe') + $jpSdkFiles | ForEach-Object { Get-JPBuildFileRecord (Join-Path $jpBinDirectory $_) })
    $jpReceipt = [ordered]@{
        format = 'lkvs-build-receipt/1'
        product_version = $jpProductVersionBefore
        built_utc = [DateTime]::UtcNow.ToString('o')
        source_files = $jpSourceRecords
        artifact_files = $jpArtifactRecords
        toolchain = [ordered]@{ go = $jpGoVersionOutput; csc = [Diagnostics.FileVersionInfo]::GetVersionInfo($jpCompilerPath).FileVersion; webview2_sdk = $jpSdkVersion; fixed_runtime = $jpRendererVersion }
    }
    $jpReceiptUtf8 = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $jpBinDirectory 'BUILD-RECEIPT.json'), (($jpReceipt | ConvertTo-Json -Depth 8) + "`n"), $jpReceiptUtf8)
    & (Join-Path $jpNativeDirectory 'refresh-manifests.ps1')
    if (-not $?) { throw 'Package manifest regeneration failed.' }
    Write-Output 'Local build complete. Build artifacts and integrity inventories do not certify portability or a clean Windows installation.'
    Write-Output 'Next: BOOT.cmd --verify, BOOT.cmd --doctor, BOOT.cmd --qualify. Record clean-machine testing separately.'
    Write-Output ('Go -work preserves intermediate compiler directories under ' + $env:TEMP + '. No build directories were deleted.')
}
finally {
    if ($jpLocationPushed) { Pop-Location }
    foreach ($jpEnvironmentName in $jpEnvironmentNames) {
        [Environment]::SetEnvironmentVariable($jpEnvironmentName, $jpEnvironmentBackup[$jpEnvironmentName], 'Process')
    }
}
