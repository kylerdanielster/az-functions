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
1. Cleans up previous test data (batch tracking + SFTP files)
2. Triggers a batch of 10 items via `POST /api/datafeed/trigger`
3. Polls batch status until all items complete
4. Verifies 20 SFTP files uploaded (10 person + 10 address)

> **Note:** `test-sftp-retry.sh` is stale — it references endpoints and tools from a previous architecture revision and will not work.

### Verifying SFTP Uploads

- **API**: `GET /api/sftp/files` and `GET /api/sftp/files/{fileName}`
- **Filesystem**: `ls ./sftp-data/`
- **SSH**: `ssh -p 2222 testuser@localhost` (password: `testpass`)

## Before Submitting Code

- E2E test passes end-to-end
- New functions have corresponding curl-based test steps
- Both happy path and error paths verified
