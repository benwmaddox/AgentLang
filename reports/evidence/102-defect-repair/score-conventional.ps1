param([string]$Project,[string]$Label)
$ErrorActionPreference='Stop'
$root='D:\code\AgentLang\.agentlang\repair-comparison-001'
$artifacts=Join-Path $root ('build-'+$Label)
$build=@(& dotnet build (Join-Path $Project 'BusinessPolicy.fsproj') -c Release --artifacts-path $artifacts --nologo 2>&1 | ForEach-Object {$_.ToString()})
$buildCode=$LASTEXITCODE
if($buildCode -ne 0){throw ($build -join "`n")}
$dll=Join-Path $artifacts 'bin/BusinessPolicy/release/BusinessPolicy.dll'
$tests=@(& dotnet $dll 2>&1 | ForEach-Object {$_.ToString()})
$testCode=$LASTEXITCODE
$input='D:\code\AgentLang\.agentlang\quick-comparison-001\results\conventional-probe-input.json'
$output=@(& dotnet $dll --probe S07 $input 2>&1 | ForEach-Object {$_.ToString()})
$probeCode=$LASTEXITCODE
if($probeCode-ne0){throw ($output -join "`n")}
$actual=($output -join "`n")|ConvertFrom-Json
$expected=Get-Content (Join-Path $root 'independent-arithmetic.json') -Raw|ConvertFrom-Json
$rows=@(foreach($case in $expected){$match=@($actual|Where-Object id -CEQ $case.id); [ordered]@{id=$case.id;expected=$case.expected;actual=if($match.Count-eq1){$match[0].value}else{$null};passed=($match.Count-eq1 -and $match[0].resultType-ceq'Money' -and $match[0].value-ceq$case.expected)}})
$result=[ordered]@{label=$Label;project=$Project;buildExit=$buildCode;build=$build;testExit=$testCode;testOutput=$tests;probeExit=$probeCode;expectedCount=$expected.Count;actualCount=$actual.Count;passedCount=@($rows|Where-Object passed).Count;cases=$rows;projectFiles=@(Get-ChildItem $Project -File -Recurse|Where-Object {$_.FullName -notmatch '[\\/](bin|obj)[\\/]'}|ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($Project,$_.FullName);sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}})}
$result|ConvertTo-Json -Depth 8|Set-Content (Join-Path $root ($Label+'-score.json'))
[pscustomobject]@{label=$Label;ownTestExit=$testCode;passed=$result.passedCount;cases=$rows.Count}