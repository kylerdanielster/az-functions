#!/bin/bash
set -e

BASE_URL="http://localhost:7071/api"

echo "============================================"
echo "  Batch Data Feed End-to-End Test"
echo "============================================"
echo ""

# Step 1: Cleanup
echo "Step 1: Cleaning up previous test data..."
echo ""

BATCH_CLEANUP=$(curl -s -X DELETE "$BASE_URL/batch")
BATCH_DELETED=$(echo "$BATCH_CLEANUP" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deleted',0))")
echo "  Batch tracking: cleared $BATCH_DELETED entities"

SFTP_CLEANUP=$(curl -s -X DELETE "$BASE_URL/sftp/files")
SFTP_DELETED=$(echo "$SFTP_CLEANUP" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deleted',0))")
echo "  SFTP files: cleared $SFTP_DELETED files"
echo ""

# Step 2: Trigger data feed
echo "--------------------------------------------"
echo "Step 2: Triggering data feed..."
echo "  POST $BASE_URL/datafeed/trigger"
echo ""

TRIGGER_RESPONSE=$(curl -s -X POST "$BASE_URL/datafeed/trigger")
BATCH_ID=$(echo "$TRIGGER_RESPONSE" | python3 -c "import sys,json; print(json.load(sys.stdin)['batchId'])")

if [ -z "$BATCH_ID" ]; then
  echo "  ERROR: Failed to get batchId from trigger response"
  echo "  Response: $TRIGGER_RESPONSE"
  exit 1
fi
echo "  Started batch $BATCH_ID with 10 items"
echo ""

# Step 3: Poll batch status until complete
echo "--------------------------------------------"
echo "Step 3: Polling batch status from Table Storage..."
echo "  GET $BASE_URL/batch/$BATCH_ID"
echo ""

FINAL_STATUS="Unknown"
for i in $(seq 1 60); do
  sleep 3
  STATUS_RESPONSE=$(curl -s "$BASE_URL/batch/$BATCH_ID")

  BATCH_STATUS=$(echo "$STATUS_RESPONSE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('Status','Unknown'))" 2>/dev/null || echo "Unknown")
  COMPLETED_COUNT=$(echo "$STATUS_RESPONSE" | python3 -c "
import sys,json
d = json.load(sys.stdin)
items = d.get('Items', [])
print(len([i for i in items if i.get('Status') in ('Completed','Failed')]))" 2>/dev/null || echo "0")
  TOTAL_COUNT=$(echo "$STATUS_RESPONSE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('ItemCount',0))" 2>/dev/null || echo "0")

  echo "  Poll $i: $COMPLETED_COUNT/$TOTAL_COUNT items done (batch: $BATCH_STATUS)"

  if [ "$BATCH_STATUS" = "Completed" ] || [ "$BATCH_STATUS" = "PartialFailure" ]; then
    FINAL_STATUS="$BATCH_STATUS"
    echo ""
    echo "  Batch $BATCH_STATUS!"
    break
  fi

  if [ "$i" -eq 60 ]; then
    FINAL_STATUS="Timeout"
    echo ""
    echo "  TIMEOUT after 3 minutes"
  fi
done
echo ""

# Display item status table from Table Storage
echo "  Item Status (from BatchTracking table):"
echo "  -------------------------------------------"
echo "$STATUS_RESPONSE" | python3 -c "
import sys, json
d = json.load(sys.stdin)
for item in d.get('Items', []):
    item_id = item.get('ItemId', '')
    status = item.get('Status', 'Unknown')
    completed = item.get('CompletedAt', '')
    error = item.get('ErrorMessage', '')
    if completed:
        completed = completed[:19]  # trim to readable length
    line = f'  {item_id:<12} {status:<12} {completed}'
    if error:
        line += f'  ERROR: {error}'
    print(line)
"
echo ""

# Step 4: Verify SFTP files
echo "--------------------------------------------"
echo "Step 4: Verifying SFTP files..."
echo ""

FILES_RESPONSE=$(curl -s "$BASE_URL/sftp/files")
FILE_COUNT=$(echo "$FILES_RESPONSE" | python3 -c "import sys,json; print(len(json.load(sys.stdin)))" 2>/dev/null || echo "0")
PERSON_COUNT=$(echo "$FILES_RESPONSE" | python3 -c "import sys,json; print(len([f for f in json.load(sys.stdin) if f['name'].startswith('person_')]))" 2>/dev/null || echo "0")
ADDRESS_COUNT=$(echo "$FILES_RESPONSE" | python3 -c "import sys,json; print(len([f for f in json.load(sys.stdin) if f['name'].startswith('address_')]))" 2>/dev/null || echo "0")

echo "  Total files: $FILE_COUNT (person: $PERSON_COUNT, address: $ADDRESS_COUNT)"
echo ""

# Read a sample file
SAMPLE_FILE=$(echo "$FILES_RESPONSE" | python3 -c "
import sys,json
files = json.load(sys.stdin)
person_files = [f for f in files if f['name'].startswith('person_')]
print(person_files[0]['name'] if person_files else '')" 2>/dev/null)

if [ -n "$SAMPLE_FILE" ]; then
  echo "  Sample file: $SAMPLE_FILE"
  echo "  Contents:"
  curl -s "$BASE_URL/sftp/files/$SAMPLE_FILE" | sed 's/^/    /'
  echo ""
fi
echo ""

# Step 5: Summary
echo "============================================"
echo "  Summary"
echo "============================================"

COMPLETED_ITEMS=$(echo "$STATUS_RESPONSE" | python3 -c "
import sys,json
d = json.load(sys.stdin)
print(len([i for i in d.get('Items', []) if i.get('Status') == 'Completed']))" 2>/dev/null || echo "0")
FAILED_ITEMS=$(echo "$STATUS_RESPONSE" | python3 -c "
import sys,json
d = json.load(sys.stdin)
print(len([i for i in d.get('Items', []) if i.get('Status') == 'Failed']))" 2>/dev/null || echo "0")

echo ""
echo "  Batch:  $FINAL_STATUS"
echo "  Items:  $COMPLETED_ITEMS completed, $FAILED_ITEMS failed"
echo "  Files:  $FILE_COUNT uploaded ($PERSON_COUNT person + $ADDRESS_COUNT address)"
echo ""

if [ "$FINAL_STATUS" = "Completed" ] && [ "$FILE_COUNT" -ge 20 ]; then
  echo "  Result: PASS"
else
  echo "  Result: FAIL"
  exit 1
fi
echo ""
echo "============================================"
