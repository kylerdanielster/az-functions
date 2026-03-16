# Git Workflow

## Branches

- `main` — production-ready code
- Feature branches: `feature/<short-description>`
- Bug fix branches: `fix/<short-description>`

## Commit Messages

Use short, imperative-mood summaries:

```
Add SFTP file upload activity
Fix null handling in person endpoint
Remove unused Storage.Queues package
```

## Pull Requests

- Keep PRs focused on a single change
- Include a summary of what changed and why
- Ensure `dotnet build` passes before opening a PR
