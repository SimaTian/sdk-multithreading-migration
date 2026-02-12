<#
.SYNOPSIS
    Reference Test Validation — Outer Loop
.DESCRIPTION
    Validates pipeline-migrated tasks against the trusted reference test suite.
    
    This script is the OUTER LOOP that runs AFTER the pipeline declares "done".
    It takes the pipeline's migrated task files from sdk-migration-test-tasks,
    remaps their namespace/class names to match FixedThreadSafeTasks, replaces
    the Fixed task source files, then runs only the Fixed reference tests.
    
    If all Fixed tests pass → the pipeline produces correct migrations.
    If any fail → pipeline skills/flow need updating before the next run.
    
    The pipeline itself never sees these reference tests.
.PARAMETER Branch
    The branch in sdk-migration-test-tasks containing pipeline output.
    Defaults to the current branch.
.PARAMETER ConfigPath
    Path to pipeline config.json.
.EXAMPLE
    .\validate-reference.ps1
    .\validate-reference.ps1 -Branch pipeline-migration-iteration-2
#>
param(
    [string]$Branch = "",
    [string]$ConfigPath = "$PSScriptRoot\config.json"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# ─── Configuration ───────────────────────────────────────────────────────────

$config = Get-Content $ConfigPath -Raw | ConvertFrom-Json
$workspaceRoot = (Resolve-Path "$PSScriptRoot\..\..\").Path.TrimEnd('\')
$migrationRepo = Join-Path $workspaceRoot $config.migrationRepo.localPath
$testTasksRepo = Join-Path $workspaceRoot $config.testTasksRepo.localPath
$mappingFile   = Join-Path $migrationRepo $config.paths.mappingFile

$fixedTasksDir = Join-Path $migrationRepo "FixedThreadSafeTasks"
$testsProject  = Join-Path $migrationRepo "UnsafeThreadSafeTasks.Tests\UnsafeThreadSafeTasks.Tests.csproj"
$tasksDir      = Join-Path $testTasksRepo "src\SdkTasks"

$mapping = Get-Content $mappingFile -Raw | ConvertFrom-Json
$tasks   = $mapping.tasks

Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host "  Reference Test Validation (Outer Loop)" -ForegroundColor Magenta
Write-Host "  Tasks: $($tasks.Count) | Mapping: $mappingFile" -ForegroundColor Magenta
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host ""

# ─── Step 0: Checkout pipeline branch if specified ───────────────────────────

if ($Branch -ne "") {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Checking out branch '$Branch' in test-tasks repo..." -ForegroundColor Yellow
    Push-Location $testTasksRepo
    try {
        git checkout $Branch 2>&1 | Out-Null
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] On branch: $Branch" -ForegroundColor Green
    } finally {
        Pop-Location
    }
}

# ─── Step 1: Backup original Fixed task files ────────────────────────────────

Write-Host ""
Write-Host "╔═══════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  Step 1: Backup Fixed Task Sources        ║" -ForegroundColor Cyan
Write-Host "╚═══════════════════════════════════════════╝" -ForegroundColor Cyan

$backupDir = Join-Path $migrationRepo "FixedThreadSafeTasks.backup"
if (Test-Path $backupDir) {
    Remove-Item $backupDir -Recurse -Force
}

# Use git stash approach - cleaner than file copy
Push-Location $migrationRepo
$stashResult = git stash push -m "validate-reference-backup" -- "FixedThreadSafeTasks/" 2>&1
$didStash = $stashResult -match "Saved working directory"
Pop-Location

# Copy all Fixed source files for backup (we'll restore from git)
Copy-Item $fixedTasksDir $backupDir -Recurse -Force
Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Backed up $((Get-ChildItem $backupDir -Filter *.cs -Recurse).Count) Fixed task source files" -ForegroundColor Green

# ─── Step 2: Remap and replace Fixed task sources ────────────────────────────

Write-Host ""
Write-Host "╔═══════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  Step 2: Remap Pipeline Output → Fixed    ║" -ForegroundColor Cyan
Write-Host "╚═══════════════════════════════════════════╝" -ForegroundColor Cyan

$remapSuccess = 0
$remapFail = 0

foreach ($task in $tasks) {
    $origFile      = $task.original.file        # e.g. "PathViolations/RelativePathToFileExists.cs"
    $origClass     = $task.original.className    # e.g. "RelativePathToFileExists"
    $origNS        = $task.original.namespace    # e.g. "UnsafeThreadSafeTasks.PathViolations"
    $origCategory  = $task.original.category     # e.g. "PathViolations"

    $disgFile      = $task.disguised.file        # e.g. "Build/FileExistenceChecker.cs"
    $disgClass     = $task.disguised.className   # e.g. "FileExistenceChecker"
    $disgNS        = $task.disguised.namespace   # e.g. "SdkTasks.Build"

    # The Fixed namespace mirrors the Unsafe one but with "Fixed" prefix
    $fixedNS       = $origNS -replace "^UnsafeThreadSafeTasks", "FixedThreadSafeTasks"

    # Source: pipeline-migrated file
    $srcPath = Join-Path $tasksDir ($disgFile -replace '/', '\')

    # Target: Fixed tasks directory (the reference "correct" implementation)
    $tgtPath = Join-Path $fixedTasksDir ($origFile -replace '/', '\')

    if (-not (Test-Path $srcPath)) {
        Write-Host "  ⚠ Missing: $disgFile" -ForegroundColor Yellow
        $remapFail++
        continue
    }

    # Read the pipeline-migrated source
    $content = Get-Content $srcPath -Raw

    # Remap namespace: SdkTasks.Build → FixedThreadSafeTasks.PathViolations
    $content = $content -replace [regex]::Escape($disgNS), $fixedNS

    # Auto-detect all class names in both files and build a mapping.
    # The pipeline may rename auxiliary classes (base classes, helpers).
    # We match them by order of declaration — both files should share structure.
    $classPattern = '(?:public|internal|private|protected)(?:\s+(?:abstract|sealed|static|partial))*\s+class\s+(\w+)'
    $fixedClassNames = @([regex]::Matches((Get-Content $tgtPath -Raw), $classPattern) | ForEach-Object { $_.Groups[1].Value })
    $pipeClassNames  = @([regex]::Matches($content, $classPattern) | ForEach-Object { $_.Groups[1].Value })

    # Build class remap: pipeline name → fixed name (positional matching)
    $classRemaps = @{}
    for ($i = 0; $i -lt [Math]::Min($pipeClassNames.Count, $fixedClassNames.Count); $i++) {
        if ($pipeClassNames[$i] -ne $fixedClassNames[$i]) {
            $classRemaps[$pipeClassNames[$i]] = $fixedClassNames[$i]
        }
    }

    # Apply all class name remappings (longest names first to avoid partial matches)
    foreach ($from in ($classRemaps.Keys | Sort-Object { $_.Length } -Descending)) {
        $to = $classRemaps[$from]
        $content = $content -replace "\b$([regex]::Escape($from))\b", $to
    }

    # Remove any disguised comment headers
    $lines = $content -split "`n"
    if ($lines[0] -match "^//.*($([regex]::Escape($disgClass))|Migrated)") {
        $lines[0] = "// FIXED: Migrated by pipeline"
    }

    $content = $lines -join "`n"

    # Ensure target directory exists
    $tgtDir = Split-Path $tgtPath -Parent
    if (-not (Test-Path $tgtDir)) {
        New-Item -ItemType Directory -Path $tgtDir -Force | Out-Null
    }

    # Write the remapped file
    Set-Content -Path $tgtPath -Value $content -NoNewline
    $remapSuccess++
}

Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Remapped: $remapSuccess OK, $remapFail missing" -ForegroundColor $(if ($remapFail -eq 0) { "Green" } else { "Yellow" })

# ─── Step 3: Build ───────────────────────────────────────────────────────────

Write-Host ""
Write-Host "╔═══════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  Step 3: Build Reference Test Suite       ║" -ForegroundColor Cyan
Write-Host "╚═══════════════════════════════════════════╝" -ForegroundColor Cyan

Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Building..." -ForegroundColor Yellow
$buildOutput = & dotnet build $testsProject --verbosity quiet 2>&1
$buildExitCode = $LASTEXITCODE

if ($buildExitCode -ne 0) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ⚠ Build has errors — these indicate structural mismatches" -ForegroundColor Yellow
    $buildErrors = @()
    $seenErrors = @{}
    $buildOutput | ForEach-Object {
        $line = $_.ToString()
        if ($line -match "(error CS\d+:.+?)(?:\s*\[|$)") {
            $errKey = $Matches[1]
            if (-not $seenErrors.ContainsKey($errKey)) {
                $seenErrors[$errKey] = $true
                Write-Host "  $line" -ForegroundColor Red
                $buildErrors += $line
            }
        }
    }
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Build errors will count as validation failures" -ForegroundColor Yellow
    Write-Host ""
} else {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ✅ Build succeeded" -ForegroundColor Green
}

# ─── Step 4: Run Fixed reference tests only ──────────────────────────────────

Write-Host ""
Write-Host "╔═══════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  Step 4: Run Fixed Reference Tests        ║" -ForegroundColor Cyan
Write-Host "╚═══════════════════════════════════════════╝" -ForegroundColor Cyan

$logDir = Join-Path $PSScriptRoot "logs\reference-validation"
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$trxFile = "reference-$timestamp.trx"
$buildErrors = if (-not $buildErrors) { @() } else { $buildErrors }

if ($buildExitCode -ne 0) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ⚠ Skipping test run — build failed" -ForegroundColor Yellow
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Build errors count as validation failures" -ForegroundColor Yellow
    $testOutput = @()
    $testExitCode = 1
} else {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Running Fixed tests (filter: _Fixed|_FixedTask)..." -ForegroundColor Yellow

    $testOutput = & dotnet test $testsProject `
        --filter "FullyQualifiedName~_Fixed" `
        --logger "trx;LogFileName=$trxFile" `
        --results-directory $logDir `
        --no-build `
        --verbosity quiet 2>&1

    $testExitCode = $LASTEXITCODE
}

# ─── Step 5: Parse and report results ────────────────────────────────────────

Write-Host ""
Write-Host "╔═══════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  Step 5: Results                          ║" -ForegroundColor Cyan
Write-Host "╚═══════════════════════════════════════════╝" -ForegroundColor Cyan

# Parse results from test output
$resultLine = $testOutput | Where-Object { $_.ToString() -match 'Failed:|Passed!' } | Select-Object -Last 1
$failedTests = @($testOutput | Where-Object { $_.ToString() -match '\[FAIL\]' })

# Extract counts
$passed = 0; $failed = 0; $total = 0
if ($resultLine) {
    $str = $resultLine.ToString()
    if ($str -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
    if ($str -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
    if ($str -match 'Total:\s*(\d+)')  { $total  = [int]$Matches[1] }
}

Write-Host ""
$totalIssues = $failed + $buildErrors.Count
if ($totalIssues -eq 0 -and $passed -gt 0) {
    Write-Host "  ╔══════════════════════════════════════════════════╗" -ForegroundColor Green
    Write-Host "  ║  ✅ ALL FIXED REFERENCE TESTS PASSED             ║" -ForegroundColor Green
    Write-Host "  ║  $passed/$total passed — Pipeline is VALIDATED    " -ForegroundColor Green
    Write-Host "  ╚══════════════════════════════════════════════════╝" -ForegroundColor Green
    Write-Host ""
    Write-Host "  The pipeline produces correct migrations." -ForegroundColor Green
    Write-Host "  Safe to use for real-world task migration." -ForegroundColor Green
} else {
    Write-Host "  ╔══════════════════════════════════════════════════╗" -ForegroundColor Red
    Write-Host "  ║  ❌ REFERENCE VALIDATION FAILED                  ║" -ForegroundColor Red
    Write-Host "  ║  $passed passed, $failed test failures, $($buildErrors.Count) build errors " -ForegroundColor Red
    Write-Host "  ╚══════════════════════════════════════════════════╝" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Pipeline needs skill/flow updates before re-run." -ForegroundColor Red

    if ($buildErrors.Count -gt 0) {
        Write-Host ""
        Write-Host "  Build errors (structural mismatches — pipeline changed API shape):" -ForegroundColor Red
        foreach ($err in $buildErrors) {
            # Extract the meaningful part
            if ($err -match '(error CS\d+:.+)$') {
                $errMsg = $Matches[1]
            } else {
                $errMsg = $err
            }
            Write-Host "    🔧 $errMsg" -ForegroundColor Red

            # Try to map error to a task
            foreach ($t in $tasks) {
                $origClass = $t.original.className
                if ($err -match [regex]::Escape($origClass)) {
                    Write-Host "       → Pipeline task: $($t.disguised.namespace).$($t.disguised.className)" -ForegroundColor DarkYellow
                    break
                }
            }
        }
    }

    if ($failedTests.Count -gt 0) {
        Write-Host ""
        Write-Host "  Failed tests:" -ForegroundColor Red
        foreach ($ft in $failedTests) {
            $ftStr = $ft.ToString().Trim()
            if ($ftStr -match '^\[xUnit.*?\]\s+(.+?)\s+\[FAIL\]') {
                $testFQN = $Matches[1]
                Write-Host "    ❌ $testFQN" -ForegroundColor Red

                $matchedTask = $null
                foreach ($t in $tasks) {
                    $origClass = $t.original.className
                    if ($testFQN -match [regex]::Escape($origClass)) {
                        $matchedTask = $t
                        break
                    }
                }
                if ($matchedTask) {
                    Write-Host "       → Pipeline task: $($matchedTask.disguised.namespace).$($matchedTask.disguised.className)" -ForegroundColor DarkYellow
                    Write-Host "       → File: $($matchedTask.disguised.file)" -ForegroundColor DarkYellow
                }
            }
        }
    }
}

# ─── Step 6: Restore original Fixed tasks ────────────────────────────────────

Write-Host ""
Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Restoring original Fixed tasks..." -ForegroundColor Yellow
Remove-Item $fixedTasksDir -Recurse -Force
Rename-Item $backupDir $fixedTasksDir
Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Restored." -ForegroundColor Green

# ─── Write summary report ────────────────────────────────────────────────────

$reportPath = Join-Path $logDir "reference-$timestamp.md"
$branchInfo = if ($Branch -ne "") { $Branch } else { "(current)" }
$reportContent = @"
# Reference Validation Report
- **Date**: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
- **Pipeline branch**: $branchInfo
- **Tasks validated**: $($tasks.Count)
- **Remapped**: $remapSuccess OK, $remapFail missing
- **Build errors**: $($buildErrors.Count)
- **Test results**: $passed/$total passed, $failed failed
- **Verdict**: $(if ($totalIssues -eq 0 -and $passed -gt 0) { "✅ PASSED — Pipeline validated" } else { "❌ FAILED — Pipeline needs updates" })

## Build Errors
$( if ($buildErrors.Count -eq 0) { "None" } else { ($buildErrors | ForEach-Object { "- ``$_``" }) -join "`n" })

## Failed Tests
$( if ($failedTests.Count -eq 0) { "None" } else {
    ($failedTests | ForEach-Object {
        $ftStr = $_.ToString().Trim()
        if ($ftStr -match '^\[xUnit.*?\]\s+(.+?)\s+\[FAIL\]') { "- $($Matches[1])" }
    }) -join "`n"
})
"@

Set-Content -Path $reportPath -Value $reportContent
Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Report: $reportPath" -ForegroundColor Cyan

Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host "  Reference Validation Complete" -ForegroundColor Magenta
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Magenta

exit $testExitCode
