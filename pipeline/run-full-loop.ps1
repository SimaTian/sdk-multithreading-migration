<#
.SYNOPSIS
    Full Loop Runner - Pipeline + Reference Validation
.DESCRIPTION
    Orchestrates the complete migration validation loop:
    1. Run the migration pipeline (run-pipeline.ps1)
    2. Validate against reference tests (validate-reference.ps1)
    3. If reference validation fails, amend skills and re-run
    4. Repeat until validated or max iterations reached
.PARAMETER MaxIterations
    Maximum outer loop iterations (default: 3)
.PARAMETER StartIteration
    Start from this iteration number (default: 1, for resuming)
.PARAMETER SkipPipeline
    Skip pipeline execution, only run reference validation (useful for testing)
.PARAMETER PipelineBranch
    Branch name pattern for pipeline output. Iteration number is appended.
    Default: 'pipeline-migration-iteration'
#>
[CmdletBinding()]
param(
    [int]$MaxIterations = 3,
    [int]$StartIteration = 1,
    [switch]$SkipPipeline,
    [string]$PipelineBranch = 'pipeline-migration-iteration'
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$LogsDir = Join-Path $ScriptDir 'logs'
$SkillsAmendmentsFile = Join-Path $ScriptDir 'skills-amendments.md'
$SummaryFile = Join-Path $LogsDir 'full-loop-summary.md'

# Load config to find test-tasks repo
$configPath = Join-Path $ScriptDir 'config.json'
$config = Get-Content $configPath -Raw | ConvertFrom-Json
$workspaceRoot = Split-Path (Split-Path $ScriptDir -Parent) -Parent
$testTasksRepo = Join-Path $workspaceRoot $config.testTasksRepo.localPath

# Ensure logs directory exists
if (-not (Test-Path $LogsDir)) {
    New-Item -ItemType Directory -Path $LogsDir -Force | Out-Null
}

# ------------------------------------------------------------------
# Helper: Write timestamped console output
# ------------------------------------------------------------------
function Write-Status {
    param([string]$Message, [string]$Color = 'Cyan')
    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Write-Host "[$timestamp] $Message" -ForegroundColor $Color
}

# ------------------------------------------------------------------
# Helper: Append to the running summary log
# ------------------------------------------------------------------
function Append-Summary {
    param([string]$Text)
    Add-Content -Path $SummaryFile -Value $Text -Encoding UTF8
}

# ------------------------------------------------------------------
# Helper: Check if a git branch exists (local or remote)
# ------------------------------------------------------------------
function Test-BranchExists {
    param([string]$Branch)
    Push-Location $testTasksRepo
    try {
        git fetch origin --quiet 2>$null
        $local = git branch --list $Branch 2>$null
        $remote = git branch -r --list "origin/$Branch" 2>$null
        return [bool]($local -or $remote)
    } finally {
        Pop-Location
    }
}

# ------------------------------------------------------------------
# Helper: Generate skills amendment from validation failures
# ------------------------------------------------------------------
function New-SkillsAmendment {
    param(
        [int]$Iteration,
        [string]$ValidationOutput
    )

    $amendmentFile = Join-Path $ScriptDir "skills-update-$Iteration.md"

    $content = @"
# Skills Amendment - Iteration $Iteration
Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')

## Reference Validation Failures

The following failures were detected during iteration $Iteration of the migration pipeline.
Address these issues in the next pipeline run.

``````
$ValidationOutput
``````

## Guidance for Next Iteration

1. Review each failing test to understand what output or behavior diverged.
2. Check whether the task migration preserved all public properties (Input/Output).
3. Verify that all path-producing Output properties resolve relative to ProjectDirectory, not CWD.
4. Ensure log messages do not contain CWD-based paths.
5. Use the reflection-based MigrationTestHarness to validate output path resolution generically.
6. Do NOT add attribute/interface-only tests - focus on behavioral correctness.
"@

    Set-Content -Path $amendmentFile -Value $content -Encoding UTF8
    Write-Status "Created skills amendment: $amendmentFile" 'Yellow'

    # Also append to the cumulative amendments file
    $separator = "`n`n---`n`n"
    if (Test-Path $SkillsAmendmentsFile) {
        Add-Content -Path $SkillsAmendmentsFile -Value "$separator$content" -Encoding UTF8
    } else {
        Set-Content -Path $SkillsAmendmentsFile -Value $content -Encoding UTF8
    }
    Write-Status "Updated cumulative skills amendments: $SkillsAmendmentsFile" 'Yellow'

    return $amendmentFile
}

# ------------------------------------------------------------------
# Initialize summary
# ------------------------------------------------------------------
$summaryHeader = @"
# Full Loop Summary
Started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
Max Iterations: $MaxIterations
Start Iteration: $StartIteration
Pipeline Branch Pattern: $PipelineBranch
Skip Pipeline: $SkipPipeline

---

"@
Set-Content -Path $SummaryFile -Value $summaryHeader -Encoding UTF8

Write-Status "========================================" 'White'
Write-Status "  Full Loop Runner - Pipeline + Validation" 'White'
Write-Status "  Max Iterations: $MaxIterations" 'White'
Write-Status "  Starting at: $StartIteration" 'White'
Write-Status "  Branch pattern: $PipelineBranch-N" 'White'
Write-Status "========================================" 'White'
Write-Host ""

$finalVerdict = 'INCOMPLETE'
$passedIteration = $null

for ($i = $StartIteration; $i -le $MaxIterations; $i++) {
    $branchName = "$PipelineBranch-$i"

    Write-Status "======== ITERATION $i / $MaxIterations ========" 'Magenta'
    Append-Summary "## Iteration $i`n"

    # ------------------------------------------------------------------
    # Phase 1: Pipeline Execution
    # ------------------------------------------------------------------
    if (-not $SkipPipeline) {
        Write-Status "Phase 1: Running migration pipeline (run-pipeline.ps1)..." 'Cyan'
        Append-Summary "### Phase 1: Pipeline Execution`n"

        $pipelineScript = Join-Path $ScriptDir 'run-pipeline.ps1'
        if (-not (Test-Path $pipelineScript)) {
            Write-Status "ERROR: run-pipeline.ps1 not found at $pipelineScript" 'Red'
            Append-Summary "**ERROR**: run-pipeline.ps1 not found. Aborting.`n"
            $finalVerdict = 'ERROR'
            break
        }

        try {
            $pipelineOutput = & $pipelineScript 2>&1 | Out-String
            $pipelineExit = $LASTEXITCODE
            Write-Status "Pipeline completed with exit code: $pipelineExit" $(if ($pipelineExit -eq 0) { 'Green' } else { 'Yellow' })
            Append-Summary "Pipeline exit code: $pipelineExit`n"

            # Save pipeline log
            $pipelineLogFile = Join-Path $LogsDir "pipeline-iteration-$i.log"
            Set-Content -Path $pipelineLogFile -Value $pipelineOutput -Encoding UTF8
            Append-Summary "Pipeline log: $pipelineLogFile`n"
        } catch {
            Write-Status "Pipeline execution failed: $_" 'Red'
            Append-Summary "**Pipeline failed**: $_`n"
            $finalVerdict = 'PIPELINE_ERROR'
            break
        }
    } else {
        Write-Status "Phase 1: SKIPPED (SkipPipeline flag set)" 'Yellow'
        Append-Summary "### Phase 1: Pipeline Execution - SKIPPED`n"
    }

    # ------------------------------------------------------------------
    # Phase 2: Check branch exists
    # ------------------------------------------------------------------
    Write-Status "Phase 2: Checking for pipeline output branch '$branchName'..." 'Cyan'

    if (-not (Test-BranchExists $branchName)) {
        Write-Status "Branch '$branchName' does not exist." 'Yellow'
        Write-Status "  The pipeline should create branch: $branchName" 'Yellow'
        Write-Status "  If running manually, create this branch with your migration changes and re-run." 'Yellow'
        Append-Summary "**Branch not found**: $branchName - waiting for pipeline output.`n"
        Append-Summary "### Action Required`nCreate branch ``$branchName`` with migration changes, then re-run with ``-StartIteration $i``.`n"
        $finalVerdict = 'WAITING_FOR_BRANCH'
        break
    }

    Write-Status "Branch '$branchName' found." 'Green'
    Append-Summary "Branch found: $branchName`n"

    # ------------------------------------------------------------------
    # Phase 3: Reference Validation
    # ------------------------------------------------------------------
    Write-Status "Phase 3: Running reference validation against '$branchName'..." 'Cyan'
    Append-Summary "### Phase 3: Reference Validation`n"

    $validateScript = Join-Path $ScriptDir 'validate-reference.ps1'
    if (-not (Test-Path $validateScript)) {
        Write-Status "ERROR: validate-reference.ps1 not found at $validateScript" 'Red'
        Append-Summary "**ERROR**: validate-reference.ps1 not found. Aborting.`n"
        $finalVerdict = 'ERROR'
        break
    }

    try {
        $prevEAP = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $validationOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $validateScript -Branch $branchName 2>&1 | Out-String
        $validationExit = $LASTEXITCODE
        $ErrorActionPreference = $prevEAP

        # Save validation log
        $validationLogFile = Join-Path $LogsDir "validation-iteration-$i.log"
        Set-Content -Path $validationLogFile -Value $validationOutput -Encoding UTF8
    } catch {
        Write-Status "Validation script threw an exception: $_" 'Red'
        $validationOutput = $_.ToString()
        $validationExit = 1
        $validationLogFile = Join-Path $LogsDir "validation-iteration-$i.log"
        Set-Content -Path $validationLogFile -Value $validationOutput -Encoding UTF8
    }

    # ------------------------------------------------------------------
    # Phase 4: Evaluate Results
    # ------------------------------------------------------------------
    if ($validationExit -eq 0) {
        Write-Status "PASS - Reference validation succeeded on iteration $i!" 'Green'
        Append-Summary "**RESULT: PASS** - All reference tests passed.`n"
        Append-Summary "Validation log: $validationLogFile`n"
        $finalVerdict = 'PASS'
        $passedIteration = $i
        break
    } else {
        Write-Status "FAIL - Reference validation failed on iteration $i." 'Red'
        Append-Summary "**RESULT: FAIL** - Reference validation detected failures.`n"
        Append-Summary "Validation log: $validationLogFile`n"

        # Generate skills amendment for next iteration
        if ($i -lt $MaxIterations) {
            Write-Status "Phase 4: Generating skills amendment for iteration $($i + 1)..." 'Yellow'
            $amendmentFile = New-SkillsAmendment -Iteration $i -ValidationOutput $validationOutput
            Append-Summary "Skills amendment: $amendmentFile`n"

            Write-Host ""
            Write-Status "--- Next Steps ---" 'Yellow'
            Write-Status "1. Review the skills amendment: $amendmentFile" 'Yellow'
            Write-Status "2. Review cumulative amendments: $SkillsAmendmentsFile" 'Yellow'
            Write-Status "3. Run the next pipeline iteration to produce branch: $PipelineBranch-$($i + 1)" 'Yellow'
            Write-Status "4. Re-run this script with: -StartIteration $($i + 1)" 'Yellow'
            Write-Host ""
        } else {
            Write-Status "Max iterations ($MaxIterations) reached without passing validation." 'Red'
            Append-Summary "`n**Max iterations reached without passing.**`n"
            $finalVerdict = 'MAX_ITERATIONS_REACHED'
        }
    }

    Append-Summary "`n---`n"
}

# ------------------------------------------------------------------
# Final Summary
# ------------------------------------------------------------------
Write-Host ""
Write-Status "========================================" 'White'
Write-Status "  FINAL VERDICT: $finalVerdict" $(
    switch ($finalVerdict) {
        'PASS'                   { 'Green' }
        'WAITING_FOR_BRANCH'     { 'Yellow' }
        'INCOMPLETE'             { 'Yellow' }
        default                  { 'Red' }
    }
)
if ($passedIteration) {
    Write-Status "  Passed on iteration: $passedIteration" 'Green'
}
Write-Status "  Summary log: $SummaryFile" 'White'
Write-Status "========================================" 'White'

$finalSummary = @"

---

## Final Verdict
- Result: $finalVerdict
- Passed Iteration: $(if ($passedIteration) { $passedIteration } else { 'N/A' })
- Completed: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
"@
Append-Summary $finalSummary

# Return appropriate exit code
if ($finalVerdict -eq 'PASS') {
    exit 0
} elseif ($finalVerdict -eq 'WAITING_FOR_BRANCH') {
    exit 2
} else {
    exit 1
}
