# Testing Standards

## End-to-End Testing

The primary test mechanism is `test-sftp-orchestration.sh`, which exercises the full orchestration flow.

### Prerequisites

1. Docker services running: `docker compose up -d`
2. Function app running: `func start` (in a separate terminal)

### Running Tests

```bash
./test-sftp-orchestration.sh
```

The script:
1. Generates fake person and address data using `tools/generate-test-data`
2. Starts an orchestration instance
3. Sends person and address data to the instance
4. Polls the status endpoint until completion
5. Lists and displays uploaded files on the SFTP server

### Test Data Generation

The `tools/generate-test-data` project uses the Bogus library to generate realistic fake data:

```bash
cd tools/generate-test-data
dotnet run -- --person    # Generate person JSON
dotnet run -- --address   # Generate address JSON
dotnet run                # Generate both
```

### Manual Testing

Use curl commands against `http://localhost:7071/api/sftp/...`. See the README for detailed step-by-step instructions.

### Verifying SFTP Uploads

- **API**: `GET /api/sftp/files` and `GET /api/sftp/files/{fileName}`
- **Filesystem**: `ls ./sftp-data/`
- **SSH**: `ssh -p 2222 testuser@localhost` (password: `testpass`)
