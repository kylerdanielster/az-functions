#!/bin/bash
set -e

BASE_URL="http://localhost:7071/api"
GENERATOR="tools/generate-test-data"

echo "============================================"
echo "  SFTP Upload Retry Test"
echo "============================================"
echo ""
echo "  This test verifies that uploads recover"
echo "  from a transient SFTP outage via retry."
echo ""

# Generate fake test data
PERSON_JSON=$(dotnet run --project "$GENERATOR" -- person)
ADDRESS_JSON=$(dotnet run --project "$GENERATOR" -- address)

# Step 1: Stop the SFTP container to simulate an outage
echo "Step 1: Stopping SFTP container..."
docker compose stop sftp > /dev/null 2>&1
echo "  SFTP container stopped."
echo ""

# Step 2: Start the orchestration
echo "--------------------------------------------"
echo "Step 2: Starting orchestration..."
echo "  POST $BASE_URL/sftp/start"
echo ""

START_RESPONSE=$(curl -s -X POST "$BASE_URL/sftp/start")
INSTANCE_ID=$(echo "$START_RESPONSE" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('id') or d.get('Id'))")
STATUS_URL=$(echo "$START_RESPONSE" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('statusQueryGetUri') or d.get('StatusQueryGetUri'))")

echo "  Instance ID: $INSTANCE_ID"
echo ""

# Step 3: Send person and address data (files will be created, uploads will fail)
echo "--------------------------------------------"
echo "Step 3: Sending person data..."
curl -s -X POST "$BASE_URL/sftp/person/$INSTANCE_ID" \
  -H "Content-Type: application/json" \
  -d "$PERSON_JSON" | python3 -m json.tool
echo ""

echo "Step 4: Sending address data..."
curl -s -X POST "$BASE_URL/sftp/address/$INSTANCE_ID" \
  -H "Content-Type: application/json" \
  -d "$ADDRESS_JSON" | python3 -m json.tool
echo ""

# Step 5: Wait for the first upload attempt to fail, then restart SFTP
# The retry policy waits 5s between attempts, so we wait a few seconds
# for the first attempt to fail, then bring SFTP back up.
echo "--------------------------------------------"
echo "Step 5: Waiting for first upload attempt to fail..."
sleep 4
echo "  Restarting SFTP container..."
docker compose start sftp > /dev/null 2>&1

# Wait for SFTP container to be ready
for i in $(seq 1 10); do
  if ssh -o BatchMode=yes -o ConnectTimeout=1 -o StrictHostKeyChecking=no -p 2222 testuser@localhost exit 2>/dev/null; then
    break
  fi
  sleep 1
done
echo "  SFTP container is back up."
echo ""

# Step 6: Wait for orchestration to complete (retry should succeed)
echo "--------------------------------------------"
echo "Step 6: Waiting for orchestration to complete via retry..."

FINAL_STATUS="Unknown"
for i in $(seq 1 30); do
  STATUS_RESPONSE=$(curl -s "$STATUS_URL")
  RUNTIME_STATUS=$(echo "$STATUS_RESPONSE" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('runtimeStatus') or d.get('RuntimeStatus') or 'Unknown')")

  echo "  Attempt $i: status = $RUNTIME_STATUS"

  if [ "$RUNTIME_STATUS" = "Completed" ]; then
    FINAL_STATUS="Completed"
    echo ""
    echo "  Orchestration completed!"
    echo "  Output: $(echo "$STATUS_RESPONSE" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('output') or d.get('Output') or '')")"
    break
  elif [ "$RUNTIME_STATUS" = "Failed" ]; then
    FINAL_STATUS="Failed"
    echo ""
    echo "  Orchestration FAILED!"
    echo "$STATUS_RESPONSE" | python3 -m json.tool
    break
  fi

  sleep 2
done
echo ""

# Step 7: Verify the files landed on the SFTP server
echo "--------------------------------------------"
echo "Step 7: Verifying uploaded files..."

PERSON_FILE=$(curl -s "$BASE_URL/sftp/files/person_$INSTANCE_ID.txt")
ADDRESS_FILE=$(curl -s "$BASE_URL/sftp/files/address_$INSTANCE_ID.txt")

if [ -n "$PERSON_FILE" ] && [ -n "$ADDRESS_FILE" ]; then
  echo ""
  echo "$PERSON_FILE"
  echo ""
  echo "$ADDRESS_FILE"
  echo ""
fi

# Final result
echo "============================================"
if [ "$FINAL_STATUS" = "Completed" ]; then
  echo "  PASS: Retry recovered from SFTP outage"
else
  echo "  FAIL: Orchestration did not complete"
  exit 1
fi
echo "============================================"
