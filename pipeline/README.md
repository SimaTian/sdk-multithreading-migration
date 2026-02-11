# Agentic Migration Pipeline

Automated harness that uses Copilot CLI agents to migrate thread-unsafe MSBuild tasks to be properly thread-safe, validate with tests, and retry until all pass.

## Prerequisites

- [GitHub Copilot CLI](https://github.com/github/copilot-cli) installed and authenticated
- .NET 8.0 SDK
- `gh` CLI (for PR creation)
- Both repos cloned as siblings in a workspace directory:
  ```
  workspace/
  ├── sdk-multithreading-migration/   (this repo)
  └── sdk-migration-test-tasks/       (target repo with tasks to migrate)
  ```

## Quick Start

```powershell
cd sdk-multithreading-migration\pipeline
.\run-pipeline.ps1
```

## How It Works

The pipeline runs in 6 phases with a retry loop:

### Phase 1: Migration Prompt Generation
A single agent reads all 46 tasks in `sdk-migration-test-tasks/src/SdkTasks/` and the skills documentation, then generates a specific migration prompt file for each task in `pipeline/prompts/`.

### Phase 2: Test Scaffolding
A single agent creates an xUnit test project (`SdkTasks.Tests`) with validation tests for all 46 tasks. Tests verify correct `TaskEnvironment` usage, interface implementation, and path resolution.

### Phase 3: Per-Task Migration
For each of the 46 tasks sequentially:
1. **Migrator agent** reads the task's prompt file and migrates the task in-place
2. **Checker agent** reviews the migration and runs the task's specific tests

### Phase 4: Final Validation
Runs the full test suite via `dotnet test` and parses TRX results.

### Phase 5: Error Analysis & Retry
If any tests fail:
1. An error analysis agent reads failures, identifies root causes, and updates the skills documentation
2. The pipeline re-runs Phase 3+4 with **all tasks** (guards against regressions)
3. Repeats until all tests pass (max 20 iterations)

### Phase 6: Finalize
- Creates a PR in `sdk-migration-test-tasks` with migrated code
- Creates a PR in `sdk-multithreading-migration` with pipeline artifacts
- Generates a detailed report

## Configuration

Edit `config.json` to customize:
- `agent.model` — AI model for agents (default: `claude-sonnet-4`)
- `agent.flags` — CLI flags for agent invocations
- Repo paths and directory names

## Resuming

To resume from a specific phase/iteration:

```powershell
.\run-pipeline.ps1 -StartPhase 3 -Iteration 2
```

## Output Structure

```
pipeline/
├── config.json
├── prompts/           # Migration prompts (Phase 1)
├── logs/              # Per-iteration agent logs
│   ├── iteration-1/
│   └── iteration-N/
├── reports/           # Final pipeline report
└── README.md
```

## Agent Invocation

Each agent is invoked as:
```
copilot -p "<prompt>" --yolo --no-ask-user --silent --model claude-sonnet-4 --add-dir <repo> --share <log>
```
