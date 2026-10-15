# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 RUSSELL PHILIP SMITHSON
[CmdletBinding(DefaultParameterSetName = 'Directory')]
param(
    [Parameter(ParameterSetName = 'Directory', Position = 0)]
    [string]$Directory = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true, ParameterSetName = 'Archive')]
    [string]$Archive,
    [Parameter(Mandatory = $true, ParameterSetName = 'Paths')]
    [string[]]$RelativePath,
    [Parameter(Mandatory = $true, ParameterSetName = 'SelfTest')]
    [switch]$SelfTest,
    [Alias('InstallRoot')]
    [string]$InstallationRoot = 'C:\LKVS',
    [string]$ReportPath,
    [Parameter(ParameterSetName = 'SelfTest')]
    [string]$FixtureDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'work\windows-path-fixtures')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This audit implements the supported Windows path policy and must run on Windows.' }
$jpPathMaximumRoot = 100
$jpPathMaximumFile = 259
$jpPathMaximumDirectory = 247

function Get-JPWindowsPathProblem([string]$RelativePath, [bool]$IsDirectory) {
    if ([string]::IsNullOrEmpty($RelativePath)) { return 'The entry path is empty.' }
    $jpName = $RelativePath.Replace('\', '/')
    if ($IsDirectory -and $jpName.EndsWith('/')) { $jpName = $jpName.Substring(0, $jpName.Length - 1) }
    if ($jpName.StartsWith('/') -or $jpName -match '^[A-Za-z]:') { return 'Absolute, drive-relative, UNC, and extended-length entry paths are forbidden.' }
    foreach ($jpPart in $jpName.Split('/')) {
        if ($jpPart.Length -eq 0 -or $jpPart -eq '.' -or $jpPart -eq '..') { return 'Empty, dot, and parent path components are forbidden.' }
        if ($jpPart.Length -gt 255) { return 'A component exceeds 255 UTF-16 code units.' }
        if ($jpPart -match '[\x00-\x1f<>:"|?*]') { return 'A component contains a Windows-invalid character or alternate data stream separator.' }
        if ($jpPart.EndsWith(' ') -or $jpPart.EndsWith('.')) { return 'A component ends in a space or dot.' }
        $jpDeviceStem = $jpPart.Split('.')[0].TrimEnd(' ')
        if ($jpDeviceStem -match '^(?i:CON|PRN|AUX|NUL|COM[1-9\u00b9\u00b2\u00b3]|LPT[1-9\u00b9\u00b2\u00b3]|CONIN\$|CONOUT\$)$') {
            return 'A component uses a reserved Windows device name.'
        }
        for ($jpCharacterIndex = 0; $jpCharacterIndex -lt $jpPart.Length; $jpCharacterIndex++) {
            $jpCharacter = $jpPart[$jpCharacterIndex]
            if ([char]::IsHighSurrogate($jpCharacter)) {
                if ($jpCharacterIndex + 1 -ge $jpPart.Length -or -not [char]::IsLowSurrogate($jpPart[$jpCharacterIndex + 1])) { return 'A component contains an unpaired UTF-16 surrogate.' }
                $jpCharacterIndex++
            }
            elseif ([char]::IsLowSurrogate($jpCharacter)) { return 'A component contains an unpaired UTF-16 surrogate.' }
        }
    }
    return $null
}

function Resolve-JPInstallationRoot([string]$Value) {
    $jpValue = $Value.Replace('/', '\')
    if ($jpValue -notmatch '^[A-Za-z]:\\') { throw 'InstallationRoot must be an absolute local drive path, for example C:\LKVS. UNC and extended-length paths are unsupported.' }
    $jpTail = $jpValue.Substring(3).TrimEnd('\')
    if ($jpTail.Length -gt 0) {
        $jpProblem = Get-JPWindowsPathProblem $jpTail $true
        if ($jpProblem) { throw ('Invalid InstallationRoot: ' + $jpProblem) }
    }
    $jpResolved = [IO.Path]::GetFullPath($jpValue)
    if ($jpResolved.Length -gt 3) { $jpResolved = $jpResolved.TrimEnd('\') }
    if ($jpResolved.Length -gt $jpPathMaximumRoot) {
        throw ('InstallationRoot exceeds the supported ' + $jpPathMaximumRoot + ' UTF-16 code-unit limit. Extract to C:\LKVS or D:\LKVS.')
    }
    return $jpResolved
}

function Get-JPDirectoryEntries([string]$Value) {
    $jpSourceRoot = [IO.Path]::GetFullPath($Value)
    if ($jpSourceRoot.Length -gt 3) { $jpSourceRoot = $jpSourceRoot.TrimEnd('\') }
    if (-not [IO.Directory]::Exists($jpSourceRoot)) { throw ('Directory does not exist: ' + $jpSourceRoot) }
    if (([IO.File]::GetAttributes($jpSourceRoot) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The audited directory itself must not be a junction or symbolic link.' }
    $jpEntries = New-Object 'Collections.Generic.List[object]'
    $jpPending = New-Object 'Collections.Generic.Queue[string]'
    $jpPending.Enqueue($jpSourceRoot)
    while ($jpPending.Count -gt 0) {
        foreach ($jpPath in [IO.Directory]::EnumerateFileSystemEntries($jpPending.Dequeue())) {
            $jpAttributes = [IO.File]::GetAttributes($jpPath)
            $jpIsDirectory = ($jpAttributes -band [IO.FileAttributes]::Directory) -ne 0
            $jpIsLink = ($jpAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            $jpRootPrefixLength = ($jpSourceRoot.TrimEnd('\') + '\').Length
            $jpEntries.Add([pscustomobject]@{ Path = $jpPath.Substring($jpRootPrefixLength).Replace('\', '/'); IsDirectory = $jpIsDirectory; IsLink = $jpIsLink })
            if ($jpIsDirectory -and -not $jpIsLink) { $jpPending.Enqueue($jpPath) }
        }
    }
    return $jpEntries.ToArray()
}

function Get-JPArchiveEntries([string]$Value) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $jpZipPath = [IO.Path]::GetFullPath($Value)
    if (-not [IO.File]::Exists($jpZipPath)) { throw ('Archive does not exist: ' + $jpZipPath) }
    $jpZip = [IO.Compression.ZipFile]::OpenRead($jpZipPath)
    try {
        $jpEntries = New-Object 'Collections.Generic.List[object]'
        foreach ($jpEntry in $jpZip.Entries) {
            $jpUnixType = ($jpEntry.ExternalAttributes -shr 16) -band 0xF000
            $jpIsDirectory = $jpEntry.FullName.EndsWith('/') -or $jpEntry.FullName.EndsWith('\') -or $jpUnixType -eq 0x4000
            $jpIsLink = $jpUnixType -eq 0xA000 -or ($jpEntry.ExternalAttributes -band 0x400) -ne 0
            $jpEntries.Add([pscustomobject]@{ Path = $jpEntry.FullName; IsDirectory = $jpIsDirectory; IsLink = $jpIsLink })
        }
        return $jpEntries.ToArray()
    }
    finally { $jpZip.Dispose() }
}

function Test-JPWindowsEntries([object[]]$Entries, [string]$Root, [string]$InputDescription) {
    $jpCheckedRoot = Resolve-JPInstallationRoot $Root
    $jpRootPrefix = $jpCheckedRoot.TrimEnd('\') + '\'
    $jpProblems = New-Object 'Collections.Generic.List[string]'
    $jpNames = New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    $jpExplicitNames = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $jpFileCount = 0
    $jpDirectoryCount = 0
    $jpLongestFile = ''
    $jpLongestDirectory = ''
    foreach ($jpEntry in $Entries) {
        $jpPath = [string]$jpEntry.Path
        $jpProblem = Get-JPWindowsPathProblem $jpPath ([bool]$jpEntry.IsDirectory)
        if ($jpProblem) { $jpProblems.Add($jpPath + ': ' + $jpProblem); continue }
        if ($jpEntry.IsLink) { $jpProblems.Add($jpPath + ': Symbolic links, junctions, and reparse points are forbidden.'); continue }
        $jpCanonical = $jpPath.Replace('\', '/').TrimEnd('/')
        if (-not $jpExplicitNames.Add($jpCanonical)) { $jpProblems.Add($jpPath + ': Duplicate or case-colliding archive entry.'); continue }
        $jpParts = $jpCanonical.Split('/')
        $jpPrefix = ''
        for ($jpPartIndex = 0; $jpPartIndex -lt $jpParts.Length; $jpPartIndex++) {
            if ($jpPrefix.Length -gt 0) { $jpPrefix += '/' }
            $jpPrefix += $jpParts[$jpPartIndex]
            $jpDirectoryPart = $jpPartIndex -lt $jpParts.Length - 1 -or [bool]$jpEntry.IsDirectory
            if ($jpNames.ContainsKey($jpPrefix)) {
                $jpExisting = $jpNames[$jpPrefix]
                if (-not [string]::Equals($jpExisting.Path, $jpPrefix, [StringComparison]::Ordinal)) { $jpProblems.Add($jpPath + ': Component spelling collides under Windows case-insensitive comparison: ' + $jpExisting.Path) }
                if ([bool]$jpExisting.IsDirectory -ne $jpDirectoryPart) { $jpProblems.Add($jpPath + ': A path is used both as a file and a directory.') }
            }
            else { $jpNames.Add($jpPrefix, [pscustomobject]@{ Path = $jpPrefix; IsDirectory = $jpDirectoryPart }) }
            $jpFullLength = $jpRootPrefix.Length + $jpPrefix.Length
            if ($jpDirectoryPart) {
                if ($jpPrefix.Length -gt $jpLongestDirectory.Length) { $jpLongestDirectory = $jpPrefix }
                if ($jpFullLength -gt $jpPathMaximumDirectory) { $jpProblems.Add($jpPath + ': Directory path exceeds ' + $jpPathMaximumDirectory + ' UTF-16 code units at the selected installation root.') }
            }
            else {
                if ($jpPrefix.Length -gt $jpLongestFile.Length) { $jpLongestFile = $jpPrefix }
                if ($jpFullLength -gt $jpPathMaximumFile) { $jpProblems.Add($jpPath + ': File path exceeds ' + $jpPathMaximumFile + ' UTF-16 code units at the selected installation root.') }
            }
        }
        if ($jpEntry.IsDirectory) { $jpDirectoryCount++ } else { $jpFileCount++ }
    }
    if ($jpProblems.Count -gt 0) { throw ('Windows path audit failed (' + $jpProblems.Count + ' violation(s)):' + [Environment]::NewLine + ($jpProblems -join [Environment]::NewLine)) }
    if ($jpFileCount -eq 0) { throw 'Windows path audit failed: the input contains no files.' }
    $jpFileRootBudget = $jpPathMaximumFile - 1 - $jpLongestFile.Length
    $jpDirectoryRootBudget = $jpPathMaximumDirectory - 1 - $jpLongestDirectory.Length
    [pscustomobject][ordered]@{
        format = 'lkvs-windows-path-audit/1'
        input = $InputDescription
        passed = $true
        installation_root = $jpCheckedRoot
        installation_root_utf16_units = $jpCheckedRoot.Length
        supported_root_maximum_utf16_units = $jpPathMaximumRoot
        file_path_maximum_utf16_units = $jpPathMaximumFile
        directory_path_maximum_utf16_units = $jpPathMaximumDirectory
        files = $jpFileCount
        explicit_directories = $jpDirectoryCount
        longest_relative_file = $jpLongestFile
        longest_relative_file_utf16_units = $jpLongestFile.Length
        longest_installed_file_utf16_units = $jpRootPrefix.Length + $jpLongestFile.Length
        longest_relative_directory = $jpLongestDirectory
        longest_relative_directory_utf16_units = $jpLongestDirectory.Length
        longest_installed_directory_utf16_units = $jpRootPrefix.Length + $jpLongestDirectory.Length
        static_root_budget_utf16_units = [Math]::Min($jpPathMaximumRoot, [Math]::Min($jpFileRootBudget, $jpDirectoryRootBudget))
        qualification = 'Path and filename audit only; this does not certify a clean-machine launch or runtime behavior.'
    }
}

function Invoke-JPWindowsPathSelfTest([string]$Value) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $jpFixtureRoot = [IO.Path]::GetFullPath($Value)
    [void][IO.Directory]::CreateDirectory($jpFixtureRoot)
    $jpCases = @(
        @{ Name = 'valid'; Paths = @('BOOT.cmd', 'runtime/webview2-fixed/locales/en-US.pak'); Pass = $true },
        @{ Name = 'backslash-valid'; Paths = @('bin\JPBroker.exe'); Pass = $true },
        @{ Name = 'reserved'; Paths = @('aux.txt'); Pass = $false },
        @{ Name = 'reserved-space-before-extension'; Paths = @('NUL .txt'); Pass = $false },
        @{ Name = 'console-input-device'; Paths = @('CONIN$.txt'); Pass = $false },
        @{ Name = 'console-output-device'; Paths = @('CONOUT$'); Pass = $false },
        @{ Name = 'reserved-superscript'; Paths = @('COM' + [char]0xB9 + '.txt'); Pass = $false },
        @{ Name = 'unicode-valid'; Paths = @(([char]::ConvertFromUtf32(0x1F600) + '/file.txt')); Pass = $true },
        @{ Name = 'trailing-dot'; Paths = @('bad./file'); Pass = $false },
        @{ Name = 'trailing-space'; Paths = @('bad /file'); Pass = $false },
        @{ Name = 'ads'; Paths = @('file.txt:stream'); Pass = $false },
        @{ Name = 'wildcard'; Paths = @('bad?.txt'); Pass = $false },
        @{ Name = 'parent'; Paths = @('../escape'); Pass = $false },
        @{ Name = 'windows-parent'; Paths = @('ok\..\escape'); Pass = $false },
        @{ Name = 'absolute'; Paths = @('/escape'); Pass = $false },
        @{ Name = 'drive-relative'; Paths = @('C:escape'); Pass = $false },
        @{ Name = 'unc'; Paths = @('\\server\share\file'); Pass = $false },
        @{ Name = 'empty-component'; Paths = @('one//two'); Pass = $false },
        @{ Name = 'case-collision'; Paths = @('One/file', 'one/other'); Pass = $false },
        @{ Name = 'duplicate'; Paths = @('one', 'one'); Pass = $false },
        @{ Name = 'file-directory-collision'; Paths = @('one', 'one/two'); Pass = $false },
        @{ Name = 'component-too-long'; Paths = @(('x' * 256)); Pass = $false },
        @{ Name = 'file-over-budget'; Paths = @((('x' * 150) + '/' + ('y' * 101))); Pass = $false },
        @{ Name = 'directory-over-budget'; Paths = @((('x' * 120) + '/' + ('y' * 122) + '/z')); Pass = $false },
        @{ Name = 'directory-entry'; Paths = @('one/', 'one/file'); Pass = $true },
        @{ Name = 'symlink'; Paths = @('link'); Pass = $false; Link = $true }
    )
    $jpPassed = 0
    foreach ($jpCase in $jpCases) {
        $jpZipPath = Join-Path $jpFixtureRoot ($jpCase.Name + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.zip')
        $jpZip = [IO.Compression.ZipFile]::Open($jpZipPath, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($jpEntryPath in $jpCase.Paths) {
                $jpEntry = $jpZip.CreateEntry($jpEntryPath)
                if ($jpCase.ContainsKey('Link') -and $jpCase.Link) { $jpEntry.ExternalAttributes = -1610612736 }
                if (-not $jpEntryPath.EndsWith('/')) { $jpStream = $jpEntry.Open(); try { $jpStream.WriteByte(120) } finally { $jpStream.Dispose() } }
            }
        }
        finally { $jpZip.Dispose() }
        $jpAccepted = $false
        $jpFailure = ''
        try { $null = Test-JPWindowsEntries @(Get-JPArchiveEntries $jpZipPath) 'C:\LKVS' $jpZipPath; $jpAccepted = $true }
        catch { $jpFailure = $_.Exception.Message }
        if ($jpAccepted -ne $jpCase.Pass) { throw ('Path self-test failed: ' + $jpCase.Name + '; accepted=' + $jpAccepted + '; error=' + $jpFailure) }
        $jpPassed++
    }
    $jpBoundaryEntry = @([pscustomobject]@{ Path = ('x' * 158); IsDirectory = $false; IsLink = $false })
    $jpBoundaryRoot = 'C:\' + ('r' * 97)
    $null = Test-JPWindowsEntries $jpBoundaryEntry $jpBoundaryRoot 'exact-259-file-boundary'
    $jpPassed++
    $jpRejectedRoot = $false
    try { $null = Test-JPWindowsEntries $jpBoundaryEntry ($jpBoundaryRoot + 'r') 'over-100-root' } catch { $jpRejectedRoot = $true }
    if (-not $jpRejectedRoot) { throw 'Path self-test failed: a 101-unit installation root was accepted.' }
    $jpPassed++
    $jpDirectCases = @(
        @{ Name = 'exact-247-directory-boundary'; Path = (('d' * 146) + '/f'); Pass = $true },
        @{ Name = 'over-247-directory-boundary'; Path = (('d' * 147) + '/f'); Pass = $false },
        @{ Name = 'exact-259-unicode-file-boundary'; Path = (('x' * 156) + [char]::ConvertFromUtf32(0x1F600)); Pass = $true },
        @{ Name = 'over-259-unicode-file-boundary'; Path = (('x' * 157) + [char]::ConvertFromUtf32(0x1F600)); Pass = $false },
        @{ Name = 'unpaired-high-surrogate'; Path = ('bad' + [char]0xD800); Pass = $false },
        @{ Name = 'unpaired-low-surrogate'; Path = ('bad' + [char]0xDC00); Pass = $false }
    )
    foreach ($jpDirectCase in $jpDirectCases) {
        $jpAccepted = $false
        try { $null = Test-JPWindowsEntries @([pscustomobject]@{ Path = $jpDirectCase.Path; IsDirectory = $false; IsLink = $false }) $jpBoundaryRoot $jpDirectCase.Name; $jpAccepted = $true } catch { }
        if ($jpAccepted -ne $jpDirectCase.Pass) { throw ('Path self-test failed: ' + $jpDirectCase.Name + '; accepted=' + $jpAccepted) }
        $jpPassed++
    }
    [pscustomobject]@{ format = 'lkvs-windows-path-self-test/1'; passed = $true; cases = $jpPassed; fixtures = $jpFixtureRoot; qualification = 'Synthetic filename and ZIP validation only; not application qualification.' }
}

if ($SelfTest) { $jpPathResult = Invoke-JPWindowsPathSelfTest $FixtureDirectory }
elseif ($PSCmdlet.ParameterSetName -eq 'Archive') { $jpPathResult = Test-JPWindowsEntries @(Get-JPArchiveEntries $Archive) $InstallationRoot ([IO.Path]::GetFullPath($Archive)) }
elseif ($PSCmdlet.ParameterSetName -eq 'Paths') {
    $jpEntries = @($RelativePath | ForEach-Object { [pscustomobject]@{ Path = $_; IsDirectory = $_.EndsWith('/') -or $_.EndsWith('\'); IsLink = $false } })
    $jpPathResult = Test-JPWindowsEntries $jpEntries $InstallationRoot 'explicit relative path list'
}
else { $jpPathResult = Test-JPWindowsEntries @(Get-JPDirectoryEntries $Directory) $InstallationRoot ([IO.Path]::GetFullPath($Directory)) }
if ($ReportPath) {
    $jpReportFullPath = [IO.Path]::GetFullPath($ReportPath)
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $jpReportFullPath))
    [IO.File]::WriteAllText($jpReportFullPath, ($jpPathResult | ConvertTo-Json -Depth 6) + "`n", (New-Object Text.UTF8Encoding($false)))
}
$jpPathResult
