[CmdletBinding()]
param(
    [string]$JavaPath = 'java',
    [int]$Workers = 4,
    [string]$JarPath = (Join-Path $PSScriptRoot 'tla2tools.jar'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/tla-reparse')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$jar = [IO.Path]::GetFullPath($JarPath)
if (!(Test-Path -LiteralPath $jar)) { throw 'Download tla2tools.jar as described in CLAUDE.md.' }
if (!(Get-Command $JavaPath -ErrorAction SilentlyContinue)) { throw 'Java 11+ is required.' }
if ($Workers -lt 1) { throw 'Workers must be positive.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$models = @(
    @{ Name='mutation'; Module='ReparseMutation.tla'; Config='ReparseMutation.cfg'; ExpectedViolation=$null },
    @{ Name='resolution'; Module='ReparseResolution.tla'; Config='ReparseResolution.cfg'; ExpectedViolation=$null },
    @{ Name='file-lock-counterexample'; Module='ReparseMutation.tla'; Config='ReparseMutation_BugFile.cfg'; ExpectedViolation='FileSetWasEmpty' },
    @{ Name='directory-lock-counterexample'; Module='ReparseMutation.tla'; Config='ReparseMutation_BugDirectory.cfg'; ExpectedViolation='DirectoryLinkHasNoChildren' }
)
$results = @()
Push-Location $PSScriptRoot
try {
    foreach ($model in $models) {
        $log = Join-Path $output ($model.Name + '.log')
        $states = Join-Path $output ($model.Name + '-states-' + [Guid]::NewGuid().ToString('N'))
        & $JavaPath -XX:+UseParallelGC -Xmx2g -jar $jar -workers $Workers -noGenerateSpecTE `
            -metadir $states -config $model.Config $model.Module 2>&1 | Tee-Object -FilePath $log
        $code = $LASTEXITCODE
        $text = Get-Content -LiteralPath $log -Raw
        if ($model.ExpectedViolation) {
            if ($code -eq 0 -or $text -notmatch ("Invariant " + $model.ExpectedViolation + " is violated")) {
                throw "Expected counterexample was not found: $log (exit $code)"
            }
        } elseif ($code -ne 0 -or $text -notmatch 'Model checking completed. No error has been found.') {
            throw "Model verification failed: $log (exit $code)"
        }
        $results += [pscustomobject]@{
            model=$model.Name; config=$model.Config; exitCode=$code
            expectedViolation=$model.ExpectedViolation; verified=$true; log=$log
        }
    }
    $results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'results.json')
} finally { Pop-Location }
Write-Host 'Reparse safety, bounded-resolution liveness and both expected counterexamples verified.'
$global:LASTEXITCODE = 0
