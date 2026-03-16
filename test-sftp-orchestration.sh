#!/bin/bash
set -e

BASE_URL="http://localhost:7071/api"
GENERATOR="tools/generate-test-data"

echo "============================================"
echo "  SFTP Orchestration End-to-End Test"
echo "============================================"
echo ""

# Generate fake test data
PERSON_JSON=$(dotnet run --project "$GENERATOR" -- person)
ADDRESS_JSON=$(dotnet run --project "$GENERATOR" -- address)

# Step 1: Start the orchestration
echo "Step 1: Starting orchestration..."
echo "  POST $BASE_URL/sftp/start"
echo ""

START_RESPONSE=$(curl -s -X POST "$BASE_URL/sftp/start")
INSTANCE_ID=$(echo "$START_RESPONSE" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('id') or d.get('Id'))")

echo "  Instance ID: $INSTANCE_ID"
echo ""

# Step 2: Send person data
echo "--------------------------------------------"
echo "Step 2: Sending person data..."
echo "  POST $BASE_URL/sftp/person/$INSTANCE_ID"
echo ""

PERSON_RESPONSE=$(curl -s -X POST "$BASE_URL/sftp/person/$INSTANCE_ID" \
  -H "Content-Type: application/json" \
  -d "$PERSON_JSON")

echo "  Request body:"
echo "  $PERSON_JSON"
echo ""
echo "  Response:"
echo "$PERSON_RESPONSE" | python3 -m json.tool
echo ""

# Step 3: Send address data
echo "--------------------------------------------"
echo "Step 3: Sending address data..."
echo "  POST $BASE_URL/sftp/address/$INSTANCE_ID"
echo ""

ADDRESS_RESPONSE=$(curl -s -X POST "$BASE_URL/sftp/address/$INSTANCE_ID" \
  -H "Content-Type: application/json" \
  -d "$ADDRESS_JSON")

echo "  Request body:"
echo "  $ADDRESS_JSON"
echo ""
echo "  Response:"
echo "$ADDRESS_RESPONSE" | python3 -m json.tool
echo ""

# Step 4: Wait for orchestration to complete
echo "--------------------------------------------"
echo "Step 4: Waiting for orchestration to complete..."
STATUS_URL=$(echo "$START_RESPONSE" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('statusQueryGetUri') or d.get('StatusQueryGetUri'))")

for i in $(seq 1 30); do
  STATUS_RESPONSE=$(curl -s "$STATUS_URL")
  RUNTIME_STATUS=$(echo "$STATUS_RESPONSE" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('runtimeStatus') or d.get('RuntimeStatus') or 'Unknown')")

  echo "  Attempt $i: status = $RUNTIME_STATUS"

  if [ "$RUNTIME_STATUS" = "Completed" ]; then
    echo ""
    echo "  Orchestration completed!"
    echo "  Output: $(echo "$STATUS_RESPONSE" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('output') or d.get('Output') or '')")"
    break
  elif [ "$RUNTIME_STATUS" = "Failed" ]; then
    echo ""
    echo "  Orchestration FAILED!"
    echo "$STATUS_RESPONSE" | python3 -m json.tool
    exit 1
  fi

  sleep 2
done
echo ""

# Step 5: Count files on SFTP server
echo "--------------------------------------------"
echo "Step 5: Checking SFTP server..."

FILES_RESPONSE=$(curl -s "$BASE_URL/sftp/files")
FILE_COUNT=$(echo "$FILES_RESPONSE" | python3 -c "import sys,json; print(len(json.load(sys.stdin)))")
echo "  Total files on SFTP server: $FILE_COUNT"
echo ""

# Step 6: Show contents of files from this run
echo "--------------------------------------------"
echo "Step 6: Reading uploaded file contents for this run..."
echo ""

curl -s "$BASE_URL/sftp/files/person_$INSTANCE_ID.txt"
echo ""
echo ""
curl -s "$BASE_URL/sftp/files/address_$INSTANCE_ID.txt"
echo ""

echo "============================================"
echo "  Test complete!"
echo "============================================"
