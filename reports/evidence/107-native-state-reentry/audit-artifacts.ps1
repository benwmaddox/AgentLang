param([Parameter(Mandatory)][string]$ArtifactRoot,[Parameter(Mandatory)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
$ArtifactRoot=[IO.Path]::GetFullPath($ArtifactRoot)
$EvidenceRoot=[IO.Path]::GetFullPath($EvidenceRoot)
$sourceRoot=Join-Path (Get-Location) 'src/AgentLang.Llvm/native'
$expectedC=(Get-FileHash "$sourceRoot/arena_runtime.c").Hash
$expectedH=(Get-FileHash "$sourceRoot/arena_runtime.h").Hash
$dlls=@(Get-ChildItem -LiteralPath $ArtifactRoot -Recurse -Filter '*.dll')
$rows=@(foreach($dll in $dlls){
 $bytes=[IO.File]::ReadAllBytes($dll.FullName)
 $pe=[BitConverter]::ToInt32($bytes,60)
 if([BitConverter]::ToUInt32($bytes,$pe) -ne 17744){throw 'Invalid PE signature'}
 $optional=$pe+24
 if([BitConverter]::ToUInt16($bytes,$optional) -ne 523){throw 'Expected PE32+'}
 $importRva=[BitConverter]::ToUInt32($bytes,$optional+120)
 $importSize=[BitConverter]::ToUInt32($bytes,$optional+124)
 $clrRva=[BitConverter]::ToUInt32($bytes,$optional+224)
 $clrSize=[BitConverter]::ToUInt32($bytes,$optional+228)
 $c=Join-Path $dll.DirectoryName 'native-runtime/arena_runtime.c'
 $h=Join-Path $dll.DirectoryName 'native-runtime/arena_runtime.h'
 $matches=((Get-FileHash -LiteralPath $c).Hash -eq $expectedC -and (Get-FileHash -LiteralPath $h).Hash -eq $expectedH)
 $row=[ordered]@{path=[IO.Path]::GetRelativePath($ArtifactRoot,$dll.FullName);sha256=(Get-FileHash $dll.FullName).Hash.ToLowerInvariant();bytes=$dll.Length;imports=($importRva -ne 0 -or $importSize -ne 0);clr=($clrRva -ne 0 -or $clrSize -ne 0);runtimeSourceMatches=$matches}
 if($row.imports -or $row.clr -or -not $matches){throw "Artifact audit failed: $($dll.FullName)"}
 $row
})
$rows | ConvertTo-Json -Depth 5 | Set-Content "$EvidenceRoot/native-artifacts.json"
$objects=@(Get-ChildItem -LiteralPath $ArtifactRoot -Recurse -File | Where-Object {$_.Extension -in @('.obj','.ll')})
$objects | ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($ArtifactRoot,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}} | ConvertTo-Json -Depth 4 | Set-Content "$EvidenceRoot/native-objects.json"
$archive=[IO.Compression.ZipFile]::Open((Join-Path $EvidenceRoot 'emitted-ir.zip'),[IO.Compression.ZipArchiveMode]::Create)
try{foreach($file in $objects | Where-Object {$_.Extension -eq '.ll'}){[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,[IO.Path]::GetRelativePath($ArtifactRoot,$file.FullName).Replace('\','/'),[IO.Compression.CompressionLevel]::Optimal) | Out-Null}}finally{$archive.Dispose()}
"Audited $($dlls.Count) DLLs: no imports/CLR, exact runtime sources. Archived emitted IR."