[CmdletBinding()]
param(
    [string]$JavaPath = 'java',
    [int]$Workers = 4,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/tla-lifetime')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$jar = Join-Path $PSScriptRoot 'tla2tools.jar'
if (!(Test-Path -LiteralPath $jar)) {
    throw 'Download tla2tools.jar as described in FILE_OBJECT_LIFETIME.md before running verification.'
}
if (!(Get-Command $JavaPath -ErrorAction SilentlyContinue)) {
    throw 'Java 11+ is required. Put java on PATH or pass -JavaPath with its executable path.'
}
if ($Workers -lt 1) { throw 'Workers must be positive.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$models = @(
    @{ Name='file-lifetime'; Module='FileObjectLifetime.tla'; Config='FileObjectLifetime_Minimal.cfg'; Bug=$false },
    @{ Name='memory-lifecycle'; Module='RamDiskSystem.tla'; Config='RamDiskSystem_Lifecycle.cfg'; Bug=$false },
    @{ Name='eager-disposal-counterexample'; Module='FileObjectLifetime.tla'; Config='FileObjectLifetime_BugRepro.cfg'; Bug=$true }
)
$results = @()
Push-Location $PSScriptRoot
try {
    foreach ($model in $models) {
        $log = Join-Path $output ($model.Name + '.log')
        $states = Join-Path $output ($model.Name + '-states-' + [Guid]::NewGuid().ToString('N'))
        & $JavaPath -XX:+UseParallelGC -Xmx4g -jar $jar -workers $Workers -noGenerateSpecTE `
            -metadir $states -config $model.Config $model.Module 2>&1 | Tee-Object -FilePath $log
        $code = $LASTEXITCODE
        $text = Get-Content -LiteralPath $log -Raw
        if ($model.Bug) {
            if ($code -eq 0 -or $text -notmatch 'Invariant PagingWritesSucceed is violated') {
                throw "Expected paging-write counterexample was not found: $log (exit $code)"
            }
        }
        elseif ($code -ne 0 -or $text -notmatch 'Model checking completed. No error has been found.') {
            throw "Model verification failed: $log (exit $code)"
        }
        $results += [pscustomobject]@{ model=$model.Name; config=$model.Config; exitCode=$code; expectedCounterexample=$model.Bug; verified=$true; log=$log }
    }
    $results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'results.json')
}
finally { Pop-Location }

Write-Host 'Lifetime safety, liveness, and the eager-disposal counterexample verified.'
# GitHub's pwsh wrapper forwards LASTEXITCODE; the expected negative TLC run
# must not turn a successfully verified counterexample into a failed CI job.
$global:LASTEXITCODE = 0
