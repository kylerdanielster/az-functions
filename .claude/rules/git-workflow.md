---
globs:
description: Git conventions for commits, branches, and PRs
---

# Git Workflow

These rules apply when creating commits, branches, or PRs.

## Branch Naming

- Feature: `feature/<short-description>`
- Bugfix: `fix/<short-description>`
- Refactor: `refactor/<short-description>`
- Use lowercase with hyphens: `feature/sftp-retry-logic`, `fix/null-deref-in-upload`

## Commit Messages

- Format: `<type>: <short summary>` (under 72 characters)
- Types: `feat`, `fix`, `refactor`, `test`, `docs`, `build`, `chore`
- Body (optional): explain *why*, not *what* — the diff shows the what
- One logical change per commit — don't mix refactors with features

Examples:
```
feat: add SFTP file upload activity
fix: prevent null event payload in person endpoint
test: add edge case for missing address data
refactor: extract SFTP config into shared helper
```

## Before Committing

1. `dotnet build` — zero warnings, zero errors
2. Run E2E test if orchestration flow was changed
3. No commented-out code or debug statements left in

## PR Guidelines

- Keep PRs focused — one feature or fix per PR
- PR title matches the primary commit's summary
- Description explains the *motivation*, not just the change
- Include test plan or verification steps
