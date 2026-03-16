# CLAUDE.md

This file provides core guidance to Claude Code. Detailed standards are in `docs/`.

## Design Principles

- **Dependency Inversion**: High-level modules should not depend on low-level modules. Both should depend on abstractions.
- **Open/Closed Principle**: Software entities should be open for extension but closed for modification.
- **Single Responsibility**: Each function, class, and module should have one clear purpose.
- **Fail Fast**: Check for potential errors early and raise exceptions immediately when issues occur.

## Core Development Philosophy

### KISS (Keep It Simple, Stupid)

Simplicity should be a key goal in design. Choose straightforward solutions over complex ones whenever possible. Simple solutions are easier to understand, maintain, and debug.

### YAGNI (You Aren't Gonna Need It)

Avoid building functionality on speculation. Implement features only when they are needed, not when you anticipate they might be useful in the future.

### Be Consistent

Avoid backwards compatability mappers and wrappers. Update the code to be consistent instead.

## AI Assistant Guidelines

### Context Awareness
- When implementing features, always check existing patterns first
- Prefer composition over inheritance in all designs
- Use existing utilities before creating new ones
- Check for similar functionality in other domains/features

### Common Pitfalls to Avoid
- Creating duplicate functionality
- Overwriting existing tests
- Modifying core frameworks without explicit instruction
- Adding dependencies without checking existing alternatives

## Important Notes

- **NEVER ASSUME OR GUESS** - When in doubt, ask for clarification
- **Always verify file paths and module names** before use
- **Keep CLAUDE.md updated** when adding new patterns or dependencies
- **Test your code** - No feature is complete without tests
- **Document your decisions** - Future developers (including yourself) will thank you

## Search Command Requirements

**CRITICAL**: Always use `rg` (ripgrep) instead of traditional `grep` and `find` commands:

```bash
# Don't use grep — use rg instead
rg "pattern"

# Don't use find -name — use rg --files instead
rg --files -g "*.py"
```

**Enforcement Rules:**

```
(
    r"^grep\b(?!.*\|)",
    "Use 'rg' (ripgrep) instead of 'grep' for better performance and features",
),
(
    r"^find\s+\S+\s+-name\b",
    "Use 'rg --files | rg pattern' or 'rg --files -g pattern' instead of 'find -name' for better performance",
),
```

## Conditional Standards (read only when relevant)

Before making any code changes, read `docs/CODING_STANDARDS.md`.

Before writing tests or as a validation gate after code changes, read `docs/TESTING_STANDARDS.md`.

Before planning features or exploring the codebase, read `docs/PROJECT_ARCHITECTURE.md`.


Before creating commits, branches, or PRs, read `docs/GIT_WORKFLOW.md`.

Before adding logging, debugging log output issues, or modifying logging configuration, read `docs/LOGGING_SYSTEM.md`.

## Useful Resources

- TODO: As necessary 
