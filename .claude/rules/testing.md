---
globs: "*.sh,*test*"
description: Testing standards for E2E test scripts and verification
---

# Testing Standards

These rules apply when working with test files and scripts.

## End-to-End Testing

The primary test mechanism is `test-sftp-orchestration.sh`.

### Prerequisites

1. Docker services running: `docker compose up -d`
2. Function app running: `func start` (in a separate terminal)

### Running Tests

```bash
./test-sftp-orchestration.sh
```

The script:
1. Generates fake data using `tools/generate-test-data`
2. Starts an orchestration instance
3. Sends person and address data
4. Polls status until completion
5. Verifies uploaded files on the SFTP server

### Test Data Generation

```bash
cd tools/generate-test-data
dotnet run -- --person    # Generate person JSON
dotnet run -- --address   # Generate address JSON
dotnet run                # Generate both
```

### Verifying SFTP Uploads

- **API**: `GET /api/sftp/files` and `GET /api/sftp/files/{fileName}`
- **Filesystem**: `ls ./sftp-data/`
- **SSH**: `ssh -p 2222 testuser@localhost` (password: `testpass`)

## Before Submitting Code

- E2E test passes end-to-end
- New functions have corresponding curl-based test steps
- Both happy path and error paths verified
