$ErrorActionPreference='Stop'
$jpRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$jpUtf8=New-Object Text.UTF8Encoding($false)
function Get-JPEntry([string]$Relative) {
  $jpFull=Join-Path $jpRoot $Relative
  [PSCustomObject]@{path=$Relative.Replace('\','/');bytes=(Get-Item -LiteralPath $jpFull).Length;sha256=(Get-FileHash -LiteralPath $jpFull -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$jpSealPath=Join-Path $jpRoot 'engine\ja21\runtime.integrity.json'
$jpOldSeal=[IO.File]::ReadAllText($jpSealPath) | ConvertFrom-Json
$jpEntries=New-Object Collections.Generic.List[Object]
$jpSeen=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach($jpEntry in $jpOldSeal.files) {
  if(Test-Path -LiteralPath (Join-Path $jpRoot $jpEntry.path) -PathType Leaf) {
    [void]$jpEntries.Add((Get-JPEntry $jpEntry.path));[void]$jpSeen.Add($jpEntry.path)
  }
}
foreach($jpRel in @('host/JPConsoleEntry.cs','bin/JPConsole.exe','bin/Microsoft.Web.WebView2.Core.dll','bin/Microsoft.Web.WebView2.Wpf.dll','bin/WebView2Loader.dll','source/jp250-service.ja')) {
  if($jpSeen.Add($jpRel)) { [void]$jpEntries.Add((Get-JPEntry $jpRel)) }
}
$jpSeal=[ordered]@{format='vb-ja21-runtime-seal/4';product='JA21 Photon Console 3.0.1 compiled native presenter';version='9.8.7';scope='Retained presenter ABI and source/IR; compiled entry point; no historical bootstrap or runtime provisioning. Hashes are not signatures.';files=$jpEntries.ToArray()}
[IO.File]::WriteAllText($jpSealPath,($jpSeal | ConvertTo-Json -Depth 10)+"`n",$jpUtf8)
$jpLines=New-Object Collections.Generic.List[string]
function Get-JPImmutableFiles([string]$Directory) {
  foreach($jpItem in Get-ChildItem -LiteralPath $Directory -Force) {
    $jpRelative=$jpItem.FullName.Substring($jpRoot.Length+1).Replace('\','/')
    # Prune mutable trees before recursion: compiler caches and path fixtures can
    # be large or intentionally contain paths outside the immutable budget.
    if($jpRelative -match '^(workspace|work|certs)(/|$)' -or $jpRelative -match '(^|/)\.git(/|$)' -or $jpRelative -in @('server-config.json','SHA256SUMS.txt','package.integrity.json')) { continue }
    if(($jpItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Reparse point in immutable package: '+$jpRelative) }
    if($jpItem.PSIsContainer) { Get-JPImmutableFiles $jpItem.FullName }
    else { $jpItem }
  }
}
foreach($jpFile in Get-JPImmutableFiles $jpRoot | Sort-Object FullName) {
  $jpRel=$jpFile.FullName.Substring($jpRoot.Length+1).Replace('\','/')
  $jpDigest=(Get-FileHash -LiteralPath $jpFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  [void]$jpLines.Add($jpDigest+'  '+$jpRel)
}
[IO.File]::WriteAllText((Join-Path $jpRoot 'SHA256SUMS.txt'),($jpLines -join "`n")+"`n",$jpUtf8)
Write-Output ('Presenter inner seal: '+$jpEntries.Count+' files; outer manifest: '+$jpLines.Count+' files.')
