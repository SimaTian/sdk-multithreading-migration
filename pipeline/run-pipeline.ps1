<#
.SYNOPSIS
    Agentic Migration Pipeline Harness
.DESCRIPTION
    Orchestrates Copilot CLI agents to migrate thread-unsafe MSBuild tasks
    in sdk-migration-test-tasks to be properly thread-safe, validates with
    tests, and retries until all pass.
.EXAMPLE
    .\run-pipeline.ps1
    .\run-pipeline.ps1 -StartPhase 3 -Iteration 2
#>
param(
    [int]$StartPhase = 1,
    [int]$Iteration = 1,
    [string]$ConfigPath = "$PSScriptRoot\config.json"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# ─── Configuration ───────────────────────────────────────────────────────────

$config = Get-Content $ConfigPath -Raw | ConvertFrom-Json
$workspaceRoot = (Resolve-Path "$PSScriptRoot\..\..\").Path.TrimEnd('\')
$migrationRepo = Join-Path $workspaceRoot $config.migrationRepo.localPath
$testTasksRepo = Join-Path $workspaceRoot $config.testTasksRepo.localPath
$pipelineDir   = $PSScriptRoot
$promptsDir    = Join-Path $pipelineDir "prompts"
$logsDir       = Join-Path $pipelineDir "logs"
$reportsDir    = Join-Path $pipelineDir "reports"
$mappingFile   = Join-Path $migrationRepo $config.paths.mappingFile
$model         = $config.agent.model
$agentFlags    = $config.agent.flags

# Load mapping
$mapping = Get-Content $mappingFile -Raw | ConvertFrom-Json
$tasks   = $mapping.tasks

Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Agentic Migration Pipeline" -ForegroundColor Cyan
Write-Host "  Tasks: $($tasks.Count) | Model: $model | Iteration: $Iteration" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# ─── Helper Functions ────────────────────────────────────────────────────────

function Invoke-CopilotAgent {
    param(
        [string]$Prompt,
        [string]$WorkingDir,
        [string]$LogFile,
        [string]$Label,
        [string[]]$ExtraDirs = @()
    )

    $iterLogDir = Split-Path $LogFile -Parent
    if (-not (Test-Path $iterLogDir)) {
        New-Item -ItemType Directory -Path $iterLogDir -Force | Out-Null
    }

    $ts = Get-Date -Format "HH:mm:ss"
    Write-Host "  [$ts][$Label] Starting agent..." -ForegroundColor Yellow
    Write-Host "  [$ts][$Label]   WorkingDir: $WorkingDir" -ForegroundColor DarkGray
    Write-Host "  [$ts][$Label]   LogFile:    $LogFile" -ForegroundColor DarkGray
    Write-Host "  [$ts][$Label]   Prompt:     $($Prompt.Length) chars" -ForegroundColor DarkGray

    # Write prompt to a temp file to avoid argument quoting issues
    $promptFile = "$LogFile.prompt.txt"
    $Prompt | Set-Content $promptFile -Encoding UTF8 -NoNewline

    # Build --add-dir arguments
    $addDirArgs = "--add-dir `"$WorkingDir`""
    foreach ($d in $ExtraDirs) {
        $addDirArgs += " --add-dir `"$d`""
    }

    # Write a launcher script to isolate quoting from the parent shell
    $launcherFile = "$LogFile.launcher.ps1"
    @"
Set-Location "$WorkingDir"
`$p = Get-Content "$promptFile" -Raw
& copilot -p `$p $agentFlags --model $model $addDirArgs --share "$LogFile" *> "$LogFile.stdout"
exit `$LASTEXITCODE
"@ | Set-Content $launcherFile -Encoding UTF8

    $startTime = Get-Date
    $proc = Start-Process -FilePath "pwsh" -ArgumentList @("-NoProfile", "-File", $launcherFile) `
        -NoNewWindow -Wait -PassThru
    $duration = (Get-Date) - $startTime

    $exitCode = $proc.ExitCode
    $ts = Get-Date -Format "HH:mm:ss"
    $status = if ($exitCode -eq 0) { "OK" } else { "FAIL (exit $exitCode)" }
    $color = if ($exitCode -eq 0) { "Green" } else { "Red" }
    Write-Host "  [$ts][$Label] $status ($('{0:mm\:ss}' -f $duration))" -ForegroundColor $color

    # Capture stdout content and show summary
    $stdout = ""
    if (Test-Path "$LogFile.stdout") {
        $stdout = Get-Content "$LogFile.stdout" -Raw -ErrorAction SilentlyContinue
        if ($stdout) {
            $lines = @($stdout -split "`n" | Where-Object { $_.Trim() })
            Write-Host "  [$ts][$Label]   Output: $($lines.Count) lines" -ForegroundColor DarkGray
            # Show last non-empty line as summary
            $lastLine = $lines[-1].Trim()
            if ($lastLine.Length -gt 120) { $lastLine = $lastLine.Substring(0, 117) + "..." }
            Write-Host "  [$ts][$Label]   Last:   $lastLine" -ForegroundColor DarkGray
        }
    }

    # Clean up temp files
    Remove-Item $promptFile -ErrorAction SilentlyContinue
    Remove-Item $launcherFile -ErrorAction SilentlyContinue

    return @{
        ExitCode = $exitCode
        Duration = $duration
        LogFile  = $LogFile
        Stdout   = $stdout
    }
}

function Get-IterationLogDir {
    param([int]$Iter)
    return Join-Path $logsDir "iteration-$Iter"
}

# ─── Parallel Agent Execution ────────────────────────────────────────────────

function Invoke-CopilotAgentAsync {
    param(
        [string]$Prompt,
        [string]$WorkingDir,
        [string]$LogFile,
        [string]$Label,
        [string[]]$ExtraDirs = @()
    )

    $iterLogDir = Split-Path $LogFile -Parent
    if (-not (Test-Path $iterLogDir)) {
        New-Item -ItemType Directory -Path $iterLogDir -Force | Out-Null
    }

    # Write prompt to a temp file
    $promptFile = "$LogFile.prompt.txt"
    $Prompt | Set-Content $promptFile -Encoding UTF8 -NoNewline

    # Build --add-dir arguments
    $addDirArgs = "--add-dir `"$WorkingDir`""
    foreach ($d in $ExtraDirs) {
        $addDirArgs += " --add-dir `"$d`""
    }

    # Write a launcher script
    $launcherFile = "$LogFile.launcher.ps1"
    @"
Set-Location "$WorkingDir"
`$p = Get-Content "$promptFile" -Raw
& copilot -p `$p $agentFlags --model $model $addDirArgs --share "$LogFile" *> "$LogFile.stdout"
exit `$LASTEXITCODE
"@ | Set-Content $launcherFile -Encoding UTF8

    $startTime = Get-Date
    $proc = Start-Process -FilePath "pwsh" -ArgumentList @("-NoProfile", "-File", $launcherFile) `
        -NoNewWindow -PassThru

    return @{
        Process     = $proc
        StartTime   = $startTime
        Label       = $Label
        LogFile     = $LogFile
        PromptFile  = $promptFile
        LauncherFile = $launcherFile
    }
}

function Wait-CopilotAgents {
    param(
        [array]$Jobs,
        [int]$PollIntervalSec = 10
    )

    $results = @()
    $pending = [System.Collections.ArrayList]@($Jobs)
    $total = $Jobs.Count

    while ($pending.Count -gt 0) {
        $completed = @()
        foreach ($job in $pending) {
            if ($job.Process.HasExited) {
                $completed += $job
            }
        }

        foreach ($job in $completed) {
            $pending.Remove($job) | Out-Null
            $duration = (Get-Date) - $job.StartTime
            $exitCode = $job.Process.ExitCode
            $ts = Get-Date -Format "HH:mm:ss"
            $status = if ($exitCode -eq 0) { "OK" } else { "FAIL (exit $exitCode)" }
            $color = if ($exitCode -eq 0) { "Green" } else { "Red" }
            Write-Host "  [$ts][$($job.Label)] $status ($('{0:mm\:ss}' -f $duration)) [$($total - $pending.Count)/$total done]" -ForegroundColor $color

            # Capture stdout
            $stdout = ""
            if (Test-Path "$($job.LogFile).stdout") {
                $stdout = Get-Content "$($job.LogFile).stdout" -Raw -ErrorAction SilentlyContinue
            }

            # Clean up temp files
            Remove-Item $job.PromptFile -ErrorAction SilentlyContinue
            Remove-Item $job.LauncherFile -ErrorAction SilentlyContinue

            $results += @{
                ExitCode = $exitCode
                Duration = $duration
                LogFile  = $job.LogFile
                Label    = $job.Label
                Stdout   = $stdout
            }
        }

        if ($pending.Count -gt 0) {
            Start-Sleep -Seconds $PollIntervalSec
        }
    }

    return $results
}

$script:Parallelism = 5

function Parse-TestResults {
    param([string]$TrxPath)

    if (-not (Test-Path $TrxPath)) {
        return @{ Total = 0; Passed = 0; Failed = 0; FailedTests = @() }
    }

    [xml]$trx = Get-Content $TrxPath -Raw
    $ns = @{ t = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010" }
    $counters = Select-Xml -Xml $trx -XPath "//t:Counters" -Namespace $ns | Select-Object -First 1
    $results  = Select-Xml -Xml $trx -XPath "//t:UnitTestResult" -Namespace $ns

    if (-not $counters) {
        return @{ Total = 0; Passed = 0; Failed = 0; FailedTests = @() }
    }

    $failed = @()
    foreach ($r in $results) {
        if ($r.Node.outcome -eq "Failed") {
            $failed += @{
                TestName = $r.Node.testName
                Message  = $r.Node.Output.ErrorInfo.Message
                StackTrace = $r.Node.Output.ErrorInfo.StackTrace
            }
        }
    }

    return @{
        Total  = [int]$counters.Node.total
        Passed = [int]$counters.Node.passed
        Failed = [int]$counters.Node.failed
        FailedTests = $failed
    }
}

function Write-TaskReport {
    param(
        [array]$TaskResults,
        [int]$Iter,
        [hashtable]$ValidationResult,
        [string]$OutputPath
    )

    $report = @"
# Migration Pipeline Report

## Summary
- **Iteration**: $Iter
- **Total Tasks**: $($tasks.Count)
- **Final Validation**: $($ValidationResult.Passed)/$($ValidationResult.Total) passed

## Per-Task Migration Results

| Task | Category | Migration | Check |
|------|----------|-----------|-------|
"@

    foreach ($tr in $TaskResults) {
        $migStatus = if ($tr.MigrationExit -eq 0) { "✅" } else { "❌" }
        $chkStatus = if ($tr.CheckExit -eq 0) { "✅" } else { "❌" }
        $report += "| $($tr.TaskName) | $($tr.Category) | $migStatus | $chkStatus |`n"
    }

    if ($ValidationResult.FailedTests.Count -gt 0) {
        $report += "`n## Failed Tests`n`n"
        foreach ($ft in $ValidationResult.FailedTests) {
            $report += "### $($ft.TestName)`n``````$($ft.Message)``````n`n"
        }
    }

    $report | Set-Content $OutputPath -Encoding UTF8
    Write-Host "  Report saved to: $OutputPath" -ForegroundColor Gray
}

# ─── Phase 1: Migration Prompt Generation ────────────────────────────────────

function Invoke-Phase1 {
    param([int]$Iter)
    Write-Host "" 
    Write-Host "╔═══════════════════════════════════════════╗" -ForegroundColor Magenta
    Write-Host "║  Phase 1: Generate Migration Prompts      ║" -ForegroundColor Magenta
    Write-Host "╚═══════════════════════════════════════════╝" -ForegroundColor Magenta

    if (@(Get-ChildItem "$promptsDir\*.md" -ErrorAction SilentlyContinue).Count -ge $tasks.Count -and $Iter -eq 1) {
        Write-Host "  Prompts already exist ($($tasks.Count) files). Skipping." -ForegroundColor Gray
        return
    }

    # On iteration 2+, clear stale prompts so they get regenerated with updated skills
    if ($Iter -gt 1) {
        $oldPrompts = @(Get-ChildItem "$promptsDir\*.md" -ErrorAction SilentlyContinue)
        if ($oldPrompts.Count -gt 0) {
            Write-Host "  Clearing $($oldPrompts.Count) stale prompts from previous iteration..." -ForegroundColor Yellow
            $oldPrompts | Remove-Item -Force
        }
    }

    # Load skills amendments if they exist (generated by outer validation loop)
    $amendmentsContent = ""
    $amendmentsFile = Join-Path $pipelineDir "skills-amendments.md"
    if (Test-Path $amendmentsFile) {
        $amendmentsContent = Get-Content $amendmentsFile -Raw
        Write-Host "  Including skills amendments from outer validation loop" -ForegroundColor Yellow
    }

    $skillFiles = (Get-ChildItem "$testTasksRepo\skills\*.md" | ForEach-Object { $_.Name }) -join ", "

    # Split tasks into batches for parallel execution
    $batchSize = [math]::Ceiling($tasks.Count / $script:Parallelism)
    $batches = @()
    for ($b = 0; $b -lt $tasks.Count; $b += $batchSize) {
        $end = [math]::Min($b + $batchSize, $tasks.Count)
        $batches += ,@($tasks[$b..($end - 1)])
    }

    Write-Host "  Spawning $($batches.Count) parallel agents ($batchSize tasks each)..." -ForegroundColor Yellow

    $jobs = @()
    for ($bi = 0; $bi -lt $batches.Count; $bi++) {
        $batch = $batches[$bi]
        $batchTaskList = ($batch | ForEach-Object { "  - $($_.disguised.className) at $($_.disguised.file)" }) -join "`n"

        $prompt = @"
You are a migration planning agent. Your job is to analyze MSBuild tasks and produce migration prompts.

WORKSPACE: $testTasksRepo
SKILLS DOCS: Read ALL files in skills/ directory ($skillFiles)
TASKS: You are responsible for $($batch.Count) tasks:
$batchTaskList

$(if ($amendmentsContent) { "IMPORTANT - LESSONS FROM PREVIOUS ITERATIONS:`n$amendmentsContent`n" })
For EACH task file:
1. Read the task source code completely
2. Identify all forbidden API usages (Path.GetFullPath, File.*/Directory.* with relative paths, Environment.GetEnvironmentVariable, Environment.SetEnvironmentVariable, Environment.CurrentDirectory, Console.*, new ProcessStartInfo, Environment.Exit, Environment.FailFast, Process.Kill)
3. Determine migration strategy (attribute-only vs interface-based per the skills docs)
4. Write a migration prompt file to: $promptsDir\<ClassName>.md

Each prompt file must contain:
- File path of the task
- List of forbidden APIs found with line numbers
- Migration strategy (attribute-only or interface-based)
- Specific code changes needed (which API calls to replace with which TaskEnvironment methods)
- TDD steps: what test to write, expected fail reason, how to fix
- ALL public properties that must be preserved on the migrated task

Generate ALL $($batch.Count) prompt files. Do not skip any task.
"@

        $logFile = Join-Path (Get-IterationLogDir $Iter) "phase1-batch-$($bi + 1).log"
        $job = Invoke-CopilotAgentAsync -Prompt $prompt -WorkingDir $testTasksRepo `
            -LogFile $logFile -Label "P1-Batch$($bi + 1)" -ExtraDirs @($migrationRepo)
        $jobs += $job
    }

    Write-Host "  Waiting for $($jobs.Count) agents..." -ForegroundColor Yellow
    $results = Wait-CopilotAgents -Jobs $jobs
    $failed = @($results | Where-Object { $_.ExitCode -ne 0 }).Count

    $promptCount = @(Get-ChildItem "$promptsDir\*.md" -ErrorAction SilentlyContinue).Count
    Write-Host "  Generated $promptCount / $($tasks.Count) prompts ($failed agent failures)" -ForegroundColor $(if ($promptCount -ge $tasks.Count) { "Green" } else { "Yellow" })
}

# ─── Phase 2: Test Scaffolding ───────────────────────────────────────────────

function Invoke-Phase2 {
    param([int]$Iter)
    Write-Host ""
    Write-Host "╔═══════════════════════════════════════════╗" -ForegroundColor Magenta
    Write-Host "║  Phase 2: Create Validation Tests         ║" -ForegroundColor Magenta
    Write-Host "╚═══════════════════════════════════════════╝" -ForegroundColor Magenta

    $testsProject = Join-Path $testTasksRepo "tests\SdkTasks.Tests"
    if ((Test-Path "$testsProject\SdkTasks.Tests.csproj") -and $Iter -eq 1) {
        Write-Host "  Test project already exists. Skipping." -ForegroundColor Gray
        return
    }

    # Read reference test infrastructure for the prompt
    $refTestDir = Join-Path $migrationRepo "UnsafeThreadSafeTasks.Tests"
    $refInfra = Join-Path $refTestDir "Infrastructure"

    # Phase 2a: Create test project and infrastructure (single agent - must complete first)
    $infraPrompt = @"
You are a test scaffolding agent. Create the test project infrastructure for validating migrated MSBuild tasks.

WORKING REPO: $testTasksRepo
REFERENCE REPO (read-only): $migrationRepo

STEP 1: Create test project at tests/SdkTasks.Tests/
- Create SdkTasks.Tests.csproj targeting net8.0 with:
  - xunit 2.7.0, Microsoft.NET.Test.Sdk 17.9.0, xunit.runner.visualstudio
  - Project reference to ../../src/SdkTasks/SdkTasks.csproj
  - NoWarn for NU1903
  - Nullable enabled, ImplicitUsings enabled

STEP 2: Copy test infrastructure pattern from the reference repo
- Read these files from $refInfra and recreate equivalent versions in tests/SdkTasks.Tests/Infrastructure/:
  - MockBuildEngine.cs (IBuildEngine4 implementation with message capture)
  - TestHelper.cs (CreateNonCwdTempDirectory, CleanupTempDirectory)
  - TaskEnvironmentHelper.cs (factory for TaskEnvironment instances)
  - TrackingTaskEnvironment.cs (counts calls to GetAbsolutePath, GetCanonicalForm, GetEnvironmentVariable)
- Adjust namespaces to SdkTasks.Tests.Infrastructure
- The polyfills (TaskEnvironment, IMultiThreadableTask, MSBuildMultiThreadableTaskAttribute) are in src/SdkTasks/Polyfills/ - reference them via the project reference

STEP 3: Verify the test project builds (dotnet build)

Only create the project and infrastructure. Do NOT create individual test files yet.
"@

    if (-not (Test-Path "$testsProject\SdkTasks.Tests.csproj")) {
        Write-Host "  Creating test infrastructure (single agent)..." -ForegroundColor Yellow
        $logFile = Join-Path (Get-IterationLogDir $Iter) "phase2-infra.log"
        $result = Invoke-CopilotAgent -Prompt $infraPrompt -WorkingDir $testTasksRepo `
            -LogFile $logFile -Label "P2-Infra" -ExtraDirs @($migrationRepo)
    }

    # Phase 2b: Create test files in parallel batches
    $batchSize = [math]::Ceiling($tasks.Count / $script:Parallelism)
    $batches = @()
    for ($b = 0; $b -lt $tasks.Count; $b += $batchSize) {
        $end = [math]::Min($b + $batchSize, $tasks.Count)
        $batches += ,@($tasks[$b..($end - 1)])
    }

    Write-Host "  Spawning $($batches.Count) parallel agents for test file creation ($batchSize tasks each)..." -ForegroundColor Yellow

    $jobs = @()
    for ($bi = 0; $bi -lt $batches.Count; $bi++) {
        $batch = $batches[$bi]
        $batchTaskList = ($batch | ForEach-Object { "  - $($_.disguised.className) at $($_.disguised.file)" }) -join "`n"

        $testPrompt = @"
You are a test scaffolding agent. Create xUnit test classes for the following MSBuild tasks.

WORKING REPO: $testTasksRepo
REFERENCE REPO (read-only): $migrationRepo
TEST PROJECT: $testsProject (already exists with Infrastructure/ helpers)

Create one test class per task in tests/SdkTasks.Tests/:
$batchTaskList

Read the REFERENCE tests in $refTestDir to understand the patterns:
- PathViolationTests.cs, EnvironmentViolationTests.cs, ComplexViolationTests.cs, etc.

The mapping file at $mappingFile maps disguised names to original names. Use it to find the corresponding reference test.

Each test class should have tests that verify CORRECT behavior (so they PASS on properly migrated tasks):
1. Task uses TaskEnvironment methods (GetAbsolutePath/GetCanonicalForm/GetEnvironmentVariable) instead of forbidden APIs
2. Task resolves paths relative to ProjectDirectory (not process CWD)

Key test pattern:
- Create a temp directory different from process CWD (TestHelper.CreateNonCwdTempDirectory)
- Create test files in that temp dir
- Set TaskEnvironment.ProjectDirectory to that temp dir
- Use TrackingTaskEnvironment to verify TaskEnvironment methods were called
- Assert paths resolve to temp dir (not CWD)

Generate ALL $($batch.Count) test files. Do not skip any task.
"@

        $logFile = Join-Path (Get-IterationLogDir $Iter) "phase2-tests-batch-$($bi + 1).log"
        $job = Invoke-CopilotAgentAsync -Prompt $testPrompt -WorkingDir $testTasksRepo `
            -LogFile $logFile -Label "P2-Batch$($bi + 1)" -ExtraDirs @($migrationRepo)
        $jobs += $job
    }

    Write-Host "  Waiting for $($jobs.Count) test-creation agents..." -ForegroundColor Yellow
    $results = Wait-CopilotAgents -Jobs $jobs
    $failed = @($results | Where-Object { $_.ExitCode -ne 0 }).Count
    Write-Host "  Test agents complete ($failed failures)" -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Yellow" })

    # Verify build
    Write-Host "  Verifying test project builds..." -ForegroundColor Gray
    $buildOutput = & dotnet build "$testsProject\SdkTasks.Tests.csproj" --verbosity quiet 2>&1
    $buildExit = $LASTEXITCODE
    if ($buildExit -ne 0) {
        Write-Host "  Test project build FAILED" -ForegroundColor Red
        $buildOutput | Where-Object { $_ -match "error" } | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    } else {
        Write-Host "  Test project builds successfully" -ForegroundColor Green
    }
}

# ─── Phase 3: Per-Task Migration ─────────────────────────────────────────────

function Invoke-Phase3 {
    param([int]$Iter)
    Write-Host ""
    Write-Host "╔═══════════════════════════════════════════╗" -ForegroundColor Magenta
    Write-Host "║  Phase 3: Migrate Tasks (Iteration $Iter)    ║" -ForegroundColor Magenta
    Write-Host "╚═══════════════════════════════════════════╝" -ForegroundColor Magenta

    # Load skills amendments from outer validation loop (if any)
    $amendmentsContent = ""
    $amendmentsFile = Join-Path $pipelineDir "skills-amendments.md"
    if (Test-Path $amendmentsFile) {
        $amendmentsContent = Get-Content $amendmentsFile -Raw
        Write-Host "  Including skills amendments from outer validation loop" -ForegroundColor Yellow
    }

    $taskResults = @()
    $iterLogDir = Get-IterationLogDir $Iter

    # Process tasks in waves of $Parallelism
    for ($wave = 0; $wave -lt $tasks.Count; $wave += $script:Parallelism) {
        $waveEnd = [math]::Min($wave + $script:Parallelism, $tasks.Count)
        $waveTasks = @($tasks[$wave..($waveEnd - 1)])
        $waveNum = [math]::Floor($wave / $script:Parallelism) + 1
        $totalWaves = [math]::Ceiling($tasks.Count / $script:Parallelism)

        Write-Host ""
        Write-Host "  --- Wave $waveNum/${totalWaves}: Tasks $($wave + 1)-$waveEnd of $($tasks.Count) ---" -ForegroundColor White

        # Phase 3a: Launch migration agents in parallel
        $migrateJobs = @()
        foreach ($task in $waveTasks) {
            $className = $task.disguised.className
            $filePath  = $task.disguised.file -replace '/', '\'
            $category  = $task.disguised.category
            $fullTaskPath = Join-Path $testTasksRepo "src\SdkTasks\$filePath"

            $promptFile = Join-Path $promptsDir "$className.md"
            $promptContent = ""
            if (Test-Path $promptFile) {
                $promptContent = Get-Content $promptFile -Raw
            }

            $migratePrompt = @"
You are a migration agent. Migrate this MSBuild task to be properly thread-safe.

TASK FILE: $fullTaskPath
CATEGORY: $category
SKILLS: Read the skills docs in $testTasksRepo\skills\ for migration patterns.

$(if ($promptContent) { "MIGRATION GUIDANCE:`n$promptContent" } else { "Analyze the task for forbidden API usage and apply the interface-migration-template pattern." })

$(if ($amendmentsContent) { "CRITICAL LESSONS FROM PREVIOUS VALIDATION FAILURES:`n$amendmentsContent`n" })
RULES:
1. Add [MSBuildMultiThreadableTask] attribute if not present
2. Implement IMultiThreadableTask interface if task uses any forbidden APIs
3. Replace ALL forbidden API calls:
   - Path.GetFullPath(x) -> TaskEnvironment.GetAbsolutePath(x) or TaskEnvironment.GetCanonicalForm(x)
   - File.*/Directory.* with relative paths -> resolve via TaskEnvironment.GetAbsolutePath() first
   - Environment.GetEnvironmentVariable() -> TaskEnvironment.GetEnvironmentVariable()
   - Environment.SetEnvironmentVariable() -> TaskEnvironment.SetEnvironmentVariable()
   - Environment.CurrentDirectory -> TaskEnvironment.ProjectDirectory
   - new ProcessStartInfo() -> TaskEnvironment.GetProcessStartInfo()
   - Console.* -> Log.LogMessage / Log.LogWarning / Log.LogError
   - Environment.Exit/FailFast -> Log.LogError + return false
   - Process.Kill -> remove or use TaskEnvironment
4. Check ALL methods including private helpers, base classes, lambdas, LINQ, Lazy<T> factories
5. Do NOT null-check TaskEnvironment - MSBuild always provides it
6. Do NOT modify test files or any other task files
7. Save the migrated file in-place at the same path
8. PRESERVE ALL public properties - do not remove, rename, or change the type of any public property

After migrating, verify the file compiles: dotnet build $testTasksRepo\src\SdkTasks\SdkTasks.csproj --verbosity quiet
Do NOT run tests. Do NOT run the full test suite. Only build to verify compilation.
"@

            $migrateLog = Join-Path $iterLogDir "migrate-$className.log"
            $job = Invoke-CopilotAgentAsync -Prompt $migratePrompt -WorkingDir $testTasksRepo `
                -LogFile $migrateLog -Label "Mig:$className" -ExtraDirs @($migrationRepo)
            $job.TaskMeta = @{ ClassName = $className; Category = $category; FilePath = $filePath }
            $migrateJobs += $job
        }

        Write-Host "  Waiting for $($migrateJobs.Count) migration agents..." -ForegroundColor Yellow
        $migrateResults = Wait-CopilotAgents -Jobs $migrateJobs

        # Phase 3b: Launch check agents in parallel for this wave
        $checkJobs = @()
        for ($j = 0; $j -lt $waveTasks.Count; $j++) {
            $task = $waveTasks[$j]
            $className = $task.disguised.className
            $fullTaskPath = Join-Path $testTasksRepo "src\SdkTasks\$($task.disguised.file -replace '/', '\')"

            $checkPrompt = @"
You are a migration checker agent. Verify that the task migration was done correctly.

TASK FILE: $fullTaskPath
TEST PROJECT: $testTasksRepo\tests\SdkTasks.Tests\SdkTasks.Tests.csproj

IMPORTANT: Only run the test for THIS specific task. Do NOT run the full test suite.

Steps:
1. Read the migrated task file
2. Check for any remaining forbidden API calls (Path.GetFullPath, Environment.GetEnvironmentVariable, Environment.CurrentDirectory, Console.*, new ProcessStartInfo, Environment.Exit/FailFast)
3. Verify the task implements IMultiThreadableTask and has [MSBuildMultiThreadableTask]
4. Run ONLY the test for this task (do NOT run unfiltered dotnet test):
   dotnet test $testTasksRepo\tests\SdkTasks.Tests\SdkTasks.Tests.csproj --filter "FullyQualifiedName~$className" --verbosity normal --no-build
5. If the filtered test fails, read the error and fix the task file. Then rebuild and re-run the filtered test only.
6. Do NOT modify test files or other task files. Do NOT run tests for other tasks.

Report PASS or FAIL as the last line of your output.
"@

            $checkLog = Join-Path $iterLogDir "check-$className.log"
            $cjob = Invoke-CopilotAgentAsync -Prompt $checkPrompt -WorkingDir $testTasksRepo `
                -LogFile $checkLog -Label "Chk:$className" -ExtraDirs @($migrationRepo)
            $cjob.TaskMeta = @{ ClassName = $className; MigrateResult = $migrateResults[$j] }
            $checkJobs += $cjob
        }

        Write-Host "  Waiting for $($checkJobs.Count) check agents..." -ForegroundColor Yellow
        $checkResults = Wait-CopilotAgents -Jobs $checkJobs

        # Collect results for this wave
        for ($j = 0; $j -lt $waveTasks.Count; $j++) {
            $task = $waveTasks[$j]
            $migResult = $migrateResults[$j]
            $chkResult = $checkResults[$j]
            $taskResults += @{
                TaskName      = $task.disguised.className
                Category      = $task.disguised.category
                FilePath      = $task.disguised.file
                MigrationExit = $migResult.ExitCode
                CheckExit     = $chkResult.ExitCode
            }

            $migOk = $migResult.ExitCode -eq 0
            $chkOk = $chkResult.ExitCode -eq 0
            $taskStatus = if ($migOk -and $chkOk) { "PASS" } elseif ($migOk) { "CHECK FAILED" } else { "MIGRATE FAILED" }
            $color = if ($migOk -and $chkOk) { "Green" } else { "Yellow" }
            Write-Host "    $($task.disguised.className): $taskStatus" -ForegroundColor $color
        }
    }

    # Phase 3 summary
    $passed = @($taskResults | Where-Object { $_.MigrationExit -eq 0 -and $_.CheckExit -eq 0 }).Count
    $migFailed = @($taskResults | Where-Object { $_.MigrationExit -ne 0 }).Count
    $chkFailed = @($taskResults | Where-Object { $_.MigrationExit -eq 0 -and $_.CheckExit -ne 0 }).Count
    Write-Host ""
    Write-Host "  Phase 3 Summary: $passed/$($tasks.Count) passed | $migFailed migrate failures | $chkFailed check failures" -ForegroundColor $(if ($passed -eq $tasks.Count) { "Green" } else { "Yellow" })

    return $taskResults
}

# ─── Phase 4: Final Validation ───────────────────────────────────────────────

function Invoke-Phase4 {
    param([int]$Iter)
    Write-Host ""
    Write-Host "╔═══════════════════════════════════════════╗" -ForegroundColor Magenta
    Write-Host "║  Phase 4: Final Validation                ║" -ForegroundColor Magenta
    Write-Host "╚═══════════════════════════════════════════╝" -ForegroundColor Magenta

    $testsProject = Join-Path $testTasksRepo "tests\SdkTasks.Tests\SdkTasks.Tests.csproj"
    $iterLogDir = Get-IterationLogDir $Iter
    $trxPath = Join-Path $iterLogDir "test-results.trx"

    if (-not (Test-Path $testsProject)) {
        Write-Host "  Test project not found at $testsProject" -ForegroundColor Red
        return @{ Total = 0; Passed = 0; Failed = 0; FailedTests = @() }
    }

    Write-Host "  $(Get-Date -Format 'HH:mm:ss') Running full test suite..." -ForegroundColor Yellow
    $trxFileName = "test-results.trx"
    & dotnet test $testsProject --logger "trx;LogFileName=$trxFileName" --results-directory $iterLogDir --verbosity quiet 2>&1 | Out-Null
    $trxPath = Join-Path $iterLogDir $trxFileName

    $results = Parse-TestResults -TrxPath $trxPath
    $color = if ($results.Failed -eq 0) { "Green" } else { "Red" }
    Write-Host "  $(Get-Date -Format 'HH:mm:ss') Results: $($results.Passed)/$($results.Total) passed, $($results.Failed) failed" -ForegroundColor $color

    if ($results.FailedTests.Count -gt 0) {
        Write-Host "  Failed tests:" -ForegroundColor Red
        foreach ($ft in $results.FailedTests) {
            Write-Host "    - $($ft.TestName)" -ForegroundColor Red
        }
    }

    return $results
}

# ─── Phase 5: Retry Loop ─────────────────────────────────────────────────────

function Invoke-Phase5 {
    param(
        [int]$Iter,
        [hashtable]$ValidationResult
    )
    Write-Host ""
    Write-Host "╔═══════════════════════════════════════════╗" -ForegroundColor Magenta
    Write-Host "║  Phase 5: Error Analysis & Skills Update  ║" -ForegroundColor Magenta
    Write-Host "╚═══════════════════════════════════════════╝" -ForegroundColor Magenta

    $iterLogDir = Get-IterationLogDir $Iter
    $trxPath = Join-Path $iterLogDir "test-results.trx"

    $failedList = ($ValidationResult.FailedTests | ForEach-Object {
        "- $($_.TestName): $($_.Message)"
    }) -join "`n"

    $prompt = @"
You are an error analysis agent. Analyze why migration tests failed and improve the skills documentation.

WORKING REPO: $testTasksRepo
REFERENCE REPO: $migrationRepo
TEST RESULTS: $trxPath

FAILED TESTS ($($ValidationResult.Failed) failures):
$failedList

YOUR TASKS:
1. For each failed test:
   a. Read the test source code to understand what it checks
   b. Read the corresponding migrated task file
   c. Read the corresponding reference fixed task in $migrationRepo\FixedThreadSafeTasks\ (use the mapping at $mappingFile)
   d. Identify EXACTLY what the migration agent missed or did wrong

2. Update the skills documentation in $testTasksRepo\skills\ to address these gaps:
   - Add specific patterns that were missed
   - Add examples of correct migrations for the failed cases
   - Be specific: mention actual API calls, patterns, and code structures

3. Produce a summary of what was wrong and what skills were updated.

Focus on ROOT CAUSES. Common issues:
- Forbidden APIs in helper methods, lambdas, base classes, LINQ pipelines
- Path.GetFullPath used for canonicalization (should be GetCanonicalForm)
- Environment.CurrentDirectory reads (should be TaskEnvironment.ProjectDirectory)
- Static mutable state not converted to instance state
- Lazy<T> factories capturing forbidden APIs
- Console.* not replaced with Log.*
"@

    $logFile = Join-Path $iterLogDir "phase5-error-analysis.log"
    $result = Invoke-CopilotAgent -Prompt $prompt -WorkingDir $testTasksRepo `
        -LogFile $logFile -Label "Phase 5" -ExtraDirs @($migrationRepo)

    # Also copy updated skills back to migration repo
    Write-Host "  Syncing updated skills to migration repo..." -ForegroundColor Gray
    Copy-Item "$testTasksRepo\skills\*" -Destination "$migrationRepo\skills\" -Force
}

# ─── Phase 6: Finalize ───────────────────────────────────────────────────────

function Invoke-Phase6 {
    param(
        [int]$Iter,
        [array]$TaskResults,
        [hashtable]$ValidationResult
    )
    Write-Host ""
    Write-Host "╔═══════════════════════════════════════════╗" -ForegroundColor Magenta
    Write-Host "║  Phase 6: Finalize                        ║" -ForegroundColor Magenta
    Write-Host "╚═══════════════════════════════════════════╝" -ForegroundColor Magenta

    # Generate final report
    $reportPath = Join-Path $reportsDir "pipeline-report.md"
    Write-TaskReport -TaskResults $TaskResults -Iter $Iter `
        -ValidationResult $ValidationResult -OutputPath $reportPath

    # Create PR in test-tasks repo
    Write-Host "  Creating PR with migrated tasks..." -ForegroundColor Yellow
    Push-Location $testTasksRepo
    try {
        $branchName = "pipeline-migration-iteration-$Iter"
        & git checkout -b $branchName 2>$null
        & git add -A
        & git commit -m "Pipeline migration iteration ${Iter}: $($ValidationResult.Passed)/$($ValidationResult.Total) tests passing" 2>$null
        & git push origin $branchName 2>$null

        $prBody = @"
## Automated Migration Pipeline Results

- **Iteration**: $Iter
- **Tests Passing**: $($ValidationResult.Passed)/$($ValidationResult.Total)
- **Failed**: $($ValidationResult.Failed)

Generated by the agentic migration pipeline.
"@

        & gh pr create --base main --head $branchName `
            --title "Migration Pipeline: Iteration $Iter ($($ValidationResult.Passed)/$($ValidationResult.Total) passing)" `
            --body $prBody 2>$null

        Write-Host "  PR created on branch $branchName" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }

    # Push skills updates + report to migration repo
    Write-Host "  Pushing pipeline artifacts to migration repo..." -ForegroundColor Yellow
    Push-Location $migrationRepo
    try {
        $branchName = "pipeline-run-iteration-$Iter"
        & git checkout -b $branchName 2>$null
        & git add pipeline/ skills/
        & git commit -m "Pipeline run iteration ${Iter}: reports, logs, and skills updates" 2>$null
        & git push origin $branchName 2>$null
        & gh pr create --base master --head $branchName `
            --title "Pipeline Run Iteration $Iter" `
            --body "Pipeline artifacts from migration run iteration $Iter" 2>$null
        Write-Host "  Pipeline artifacts PR created" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
}

# ─── Main Pipeline Loop ─────────────────────────────────────────────────────

function Invoke-Pipeline {
    $iter = $Iteration
    $maxIter = 20  # Safety limit

    # Phase 1 & 2 only run on first iteration (or if starting from those phases)
    if ($StartPhase -le 1) {
        Invoke-Phase1 -Iter $iter
    }

    if ($StartPhase -le 2) {
        Invoke-Phase2 -Iter $iter
    }

    while ($iter -le $maxIter) {
        Write-Host ""
        Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor White
        Write-Host "  ITERATION $iter" -ForegroundColor White
        Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor White

        # Phase 3: Migrate all tasks
        $taskResults = Invoke-Phase3 -Iter $iter

        # Phase 4: Validate
        $validation = Invoke-Phase4 -Iter $iter

        if ($validation.Failed -eq 0 -and $validation.Total -gt 0) {
            Write-Host ""
            Write-Host "  ★ ALL TESTS PASSING! Pipeline complete. ★" -ForegroundColor Green
            Invoke-Phase6 -Iter $iter -TaskResults $taskResults -ValidationResult $validation
            break
        }

        if ($iter -ge $maxIter) {
            Write-Host ""
            Write-Host "  ✗ Max iterations ($maxIter) reached. Finalizing with current state." -ForegroundColor Red
            Invoke-Phase6 -Iter $iter -TaskResults $taskResults -ValidationResult $validation
            break
        }

        # Phase 5: Analyze errors and update skills
        Invoke-Phase5 -Iter $iter -ValidationResult $validation

        $iter++
        Write-Host ""
        Write-Host "  → Retrying with updated skills (iteration $iter)..." -ForegroundColor Cyan
    }

    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "  Pipeline finished after $iter iteration(s)" -ForegroundColor Cyan
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
}

# Run
Invoke-Pipeline
