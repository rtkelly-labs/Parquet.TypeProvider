# PARQUET.TYPEPROVIDER

## 🛑 Repository Conventions & Workflow Policy

1. **Squash Merge Only**: All pull requests must be merged into `main` using **Squash and Merge** exclusively.
2. **Delete Branch on Merge**: Feature branches must be automatically deleted immediately upon merge into `main`.
3. **Linear History**: Maintain a strictly linear history. Rebase feature branches onto `main` before merging.
4. **Direct Push Protection**: Direct pushes to `main` are blocked; PR mechanism required (force push allowed).
5. **Local Temp & Worktree Directory**: All temporary files, databases, and worktrees go in `/temp/` (gitignored).
6. **Gitignored Local TODO File**: A root `TODO.md` file MUST exist for local task tracking and be gitignored.
7. **Auto-Merge Enabled**: PRs may enable auto-merge (squash) so they merge automatically once required checks pass.
8. **Required Status Checks**: The short, stable check identifiers for branch protection are `build` (from `ci.yml`) and `pr-title` (from `pr-title.yml`).
9. **Script Language Policy**: Automation scripts in `scripts/` MUST use the repository's approved language stack (e.g., C# `.cs` or F# `.fsx` for .NET). Alien scripting runtimes (e.g., `.sh`/`.ps1` everywhere, unauthorized `.py`) are strictly prohibited and gated by CI.
