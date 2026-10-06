# Read-only reconciliation of repeat-001 metrics against the saved JSONL traces.
# Run only after the corresponding actors have finished writing their artifacts.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$repeatRoot = Join-Path $repositoryRoot 'experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001'
$trialRows = [System.Collections.Generic.List[object]]::new()

function Get-OptionalProperty {
    param(
        [Parameter(Mandatory = $true)] [AllowNull()] [object] $Object,
        [Parameter(Mandatory = $true)] [string] $Name
    )

    if ($null -eq $Object) {
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

foreach ($arm in @('conventional', 'growing', 'flat')) {
    foreach ($task in 1..5) {
        $trialName = "$arm-task-$task"
        $trialRoot = Join-Path $repeatRoot $trialName
        $metricsPath = Join-Path $trialRoot 'metrics.json'
        $acceptancePath = Join-Path $trialRoot 'acceptance.json'
        $tracePath = Join-Path $trialRoot 'trace.jsonl'

        if (-not (Test-Path -LiteralPath $metricsPath) -or
            -not (Test-Path -LiteralPath $acceptancePath) -or
            -not (Test-Path -LiteralPath $tracePath)) {
            $trialRows.Add([pscustomobject]@{
                Trial = $trialName
                Status = 'pending'
                Exchanges = $null
                RequestBytes = $null
                ResponseBytes = $null
                Errors = $null
                ErrorDetails = @()
                MetricsMatch = $null
            })
            continue
        }

        $acceptance = Get-Content -LiteralPath $acceptancePath -Raw | ConvertFrom-Json
        $metrics = Get-Content -LiteralPath $metricsPath -Raw | ConvertFrom-Json
        if ((Get-OptionalProperty -Object $acceptance -Name 'passed') -ne $true -or
            (Get-OptionalProperty -Object $metrics -Name 'accepted') -ne $true) {
            $trialRows.Add([pscustomobject]@{
                Trial = $trialName
                Status = 'pending'
                Exchanges = $null
                RequestBytes = $null
                ResponseBytes = $null
                Errors = $null
                ErrorDetails = @()
                MetricsMatch = $null
            })
            continue
        }

        $exchangeRows = [System.Collections.Generic.List[object]]::new()
        foreach ($line in Get-Content -LiteralPath $tracePath) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }
            $event = $line | ConvertFrom-Json
            if ((Get-OptionalProperty -Object $event -Name 'event') -ne 'exchange') {
                continue
            }

            $response = Get-OptionalProperty -Object $event -Name 'response'
            $responseBytes = if ($null -ne $response) {
                Get-OptionalProperty -Object $response -Name 'payloadUtf8Bytes'
            } else {
                0
            }
            if ($null -eq $responseBytes) {
                $responseBytes = 0
            }

            $errorCode = Get-OptionalProperty -Object $event -Name 'errorCode'
            $responseLine = if ($null -ne $response) {
                Get-OptionalProperty -Object $response -Name 'rawLine'
            } else {
                $null
            }
            if ([string]::IsNullOrWhiteSpace([string] $errorCode) -and
                -not [string]::IsNullOrWhiteSpace([string] $responseLine)) {
                $responseObject = $responseLine | ConvertFrom-Json
                if ((Get-OptionalProperty -Object $responseObject -Name 'ok') -eq $false) {
                    $runtimeError = Get-OptionalProperty -Object $responseObject -Name 'error'
                    $errorCode = if ($null -ne $runtimeError) {
                        Get-OptionalProperty -Object $runtimeError -Name 'code'
                    } else {
                        $null
                    }
                    if ([string]::IsNullOrWhiteSpace([string] $errorCode)) {
                        $errorCode = 'UNSPECIFIED_RESPONSE_ERROR'
                    }
                }
            }

            $request = Get-OptionalProperty -Object $event -Name 'request'
            $requestBytes = Get-OptionalProperty -Object $request -Name 'payloadUtf8Bytes'
            if ($null -eq $requestBytes) {
                $requestBytes = 0
            }
            $exchangeRows.Add([pscustomobject]@{
                Index = Get-OptionalProperty -Object $event -Name 'index'
                RequestBytes = $requestBytes
                ResponseBytes = $responseBytes
                ErrorCode = $errorCode
                Operation = Get-OptionalProperty -Object $event -Name 'operation'
            })
        }

        $requestBytes = [long](($exchangeRows | Measure-Object -Property RequestBytes -Sum).Sum)
        $responseBytes = [long](($exchangeRows | Measure-Object -Property ResponseBytes -Sum).Sum)
        $errors = @($exchangeRows | Where-Object { -not [string]::IsNullOrWhiteSpace([string] $_.ErrorCode) })
        $errorDetails = @($errors | ForEach-Object {
            '{0}:{1}:{2}' -f $_.Index, $_.Operation, $_.ErrorCode
        })

        $metricsErrors = @(Get-OptionalProperty -Object $metrics -Name 'errors')
        $metricsErrorDetails = @($metricsErrors | ForEach-Object {
            '{0}:{1}:{2}' -f $_.index, $_.operation, $_.code
        })
        $metricsMatch = (
            [int](Get-OptionalProperty -Object $metrics -Name 'exchanges') -eq $exchangeRows.Count -and
            [long](Get-OptionalProperty -Object $metrics -Name 'requestPayloadUtf8Bytes') -eq $requestBytes -and
            [long](Get-OptionalProperty -Object $metrics -Name 'responsePayloadUtf8Bytes') -eq $responseBytes -and
            [int](Get-OptionalProperty -Object $metrics -Name 'runtimeErrors') -eq $errors.Count -and
            (@($metricsErrorDetails | Sort-Object) -join '|') -ceq (@($errorDetails | Sort-Object) -join '|')
        )

        $trialRows.Add([pscustomobject]@{
            Trial = $trialName
            Status = 'accepted'
            Exchanges = $exchangeRows.Count
            RequestBytes = $requestBytes
            ResponseBytes = $responseBytes
            Errors = $errors.Count
            ErrorDetails = $errorDetails
            MetricsMatch = $metricsMatch
        })
    }
}

$acceptedRows = @($trialRows | Where-Object { $_.Status -eq 'accepted' })
$pendingRows = @($trialRows | Where-Object { $_.Status -ne 'accepted' })
$taskTotals = foreach ($task in 1..5) {
    $rows = @($acceptedRows | Where-Object { $_.Trial -match "-task-$task$" })
    if ($rows.Count -eq 3) {
        [pscustomobject]@{
            Task = $task
            Exchanges = [long](($rows | Measure-Object -Property Exchanges -Sum).Sum)
            RequestBytes = [long](($rows | Measure-Object -Property RequestBytes -Sum).Sum)
            ResponseBytes = [long](($rows | Measure-Object -Property ResponseBytes -Sum).Sum)
            Errors = [long](($rows | Measure-Object -Property Errors -Sum).Sum)
        }
    }
}
$armTotals = foreach ($arm in @('conventional', 'growing', 'flat')) {
    $rows = @($acceptedRows | Where-Object { $_.Trial -like "$arm-task-*" })
    if ($rows.Count -eq 5) {
        [pscustomobject]@{
            Arm = $arm
            Exchanges = [long](($rows | Measure-Object -Property Exchanges -Sum).Sum)
            RequestBytes = [long](($rows | Measure-Object -Property RequestBytes -Sum).Sum)
            ResponseBytes = [long](($rows | Measure-Object -Property ResponseBytes -Sum).Sum)
            Errors = [long](($rows | Measure-Object -Property Errors -Sum).Sum)
        }
    }
}
$overall = if ($acceptedRows.Count -eq 15) {
    [pscustomobject]@{
        Exchanges = [long](($acceptedRows | Measure-Object -Property Exchanges -Sum).Sum)
        RequestBytes = [long](($acceptedRows | Measure-Object -Property RequestBytes -Sum).Sum)
        ResponseBytes = [long](($acceptedRows | Measure-Object -Property ResponseBytes -Sum).Sum)
        Errors = [long](($acceptedRows | Measure-Object -Property Errors -Sum).Sum)
    }
} else {
    $null
}

[pscustomobject]@{
    Study = 'repeat-001'
    AcceptedTrials = $acceptedRows.Count
    PendingTrials = @($pendingRows | ForEach-Object { $_.Trial })
    Trials = @($trialRows)
    CompleteTaskTotals = @($taskTotals)
    CompleteArmTotals = @($armTotals)
    OverallTotals = $overall
    TraceMetricsMismatchTrials = @($acceptedRows | Where-Object { $_.MetricsMatch -ne $true } | ForEach-Object { $_.Trial })
} | ConvertTo-Json -Depth 8
