#!/bin/bash
set -e

BASE_URL="http://localhost:7071/api"

echo "============================================"
echo "  Batch Payment Processing End-to-End Test"
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
echo "  Started batch $BATCH_ID with 10 payments"
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
  FILES_DONE=$(echo "$STATUS_RESPONSE" | python3 -c "
import sys,json
d = json.load(sys.stdin)
files = d.get('Files', [])
print(len([f for f in files if f.get('Status') in ('Processed','Failed','PaymentFileFailed','GLFileFailed')]))" 2>/dev/null || echo "0")
  FILES_TOTAL=$(echo "$STATUS_RESPONSE" | python3 -c "import sys,json; print(len(json.load(sys.stdin).get('Files', [])))" 2>/dev/null || echo "0")

  echo "  Poll $i: $FILES_DONE/$FILES_TOTAL files done (batch: $BATCH_STATUS)"

  if [ "$BATCH_STATUS" = "Processed" ] || [ "$BATCH_STATUS" = "PaymentFileFailed" ] || [ "$BATCH_STATUS" = "GLFileFailed" ] || [ "$BATCH_STATUS" = "Failed" ]; then
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

# Display file status table from Table Storage
echo "  File Status (from BatchTracking table):"
echo "  -------------------------------------------"
echo "$STATUS_RESPONSE" | python3 -c "
import sys, json
d = json.load(sys.stdin)
for f in d.get('Files', []):
    file_type = f.get('FileType', '')
    status = f.get('Status', 'Unknown')
    completed = f.get('CompletedAt', '')
    error = f.get('ErrorMessage', '')
    if completed:
        completed = completed[:19]
    line = f'  {file_type:<12} {status:<12} {completed}'
    if error:
        line += f'  ERROR: {error}'
    print(line)
"
echo ""

# Display payment status from Table Storage
echo "  Payment Status (from BatchTracking table):"
echo "  -------------------------------------------"
echo "$STATUS_RESPONSE" | python3 -c "
import sys, json
d = json.load(sys.stdin)
for p in d.get('Payments', []):
    pid = p.get('PaymentId', '')
    status = p.get('Status', 'Unknown')
    print(f'  {pid:<12} {status}')
"
echo ""

# Step 4: Verify SFTP files
echo "--------------------------------------------"
echo "Step 4: Verifying SFTP files..."
echo ""

FILES_RESPONSE=$(curl -s "$BASE_URL/sftp/files")
FILE_COUNT=$(echo "$FILES_RESPONSE" | python3 -c "import sys,json; print(len(json.load(sys.stdin)))" 2>/dev/null || echo "0")
PAYMENT_COUNT=$(echo "$FILES_RESPONSE" | python3 -c "import sys,json; print(len([f for f in json.load(sys.stdin) if f['name'].startswith('payment_')]))" 2>/dev/null || echo "0")
GL_COUNT=$(echo "$FILES_RESPONSE" | python3 -c "import sys,json; print(len([f for f in json.load(sys.stdin) if f['name'].startswith('gl_')]))" 2>/dev/null || echo "0")

echo "  Total files: $FILE_COUNT (payment: $PAYMENT_COUNT, gl: $GL_COUNT)"
echo ""

# Read a sample file
SAMPLE_FILE=$(echo "$FILES_RESPONSE" | python3 -c "
import sys,json
files = json.load(sys.stdin)
payment_files = [f for f in files if f['name'].startswith('payment_')]
print(payment_files[0]['name'] if payment_files else '')" 2>/dev/null)

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

PROCESSED_FILE_ENTITIES=$(echo "$STATUS_RESPONSE" | python3 -c "
import sys,json
d = json.load(sys.stdin)
print(len([f for f in d.get('Files', []) if f.get('Status') == 'Processed']))" 2>/dev/null || echo "0")
FAILED_FILE_ENTITIES=$(echo "$STATUS_RESPONSE" | python3 -c "
import sys,json
d = json.load(sys.stdin)
print(len([f for f in d.get('Files', []) if f.get('Status') == 'Failed']))" 2>/dev/null || echo "0")
PAYMENT_ENTITIES=$(echo "$STATUS_RESPONSE" | python3 -c "
import sys,json
d = json.load(sys.stdin)
print(len(d.get('Payments', [])))" 2>/dev/null || echo "0")

echo ""
echo "  Batch:  $FINAL_STATUS"
echo "  Files (tracking):  $PROCESSED_FILE_ENTITIES processed, $FAILED_FILE_ENTITIES failed"
echo "  Files (SFTP):  $FILE_COUNT uploaded ($PAYMENT_COUNT payment + $GL_COUNT gl)"
echo "  Payments tracked:  $PAYMENT_ENTITIES"
echo ""

if [ "$FINAL_STATUS" = "Processed" ] && [ "$FILE_COUNT" -ge 2 ] && [ "$PROCESSED_FILE_ENTITIES" -ge 2 ] && [ "$FAILED_FILE_ENTITIES" -eq 0 ] && [ "$PAYMENT_ENTITIES" -ge 10 ]; then
  echo "  Result: PASS"
else
  echo "  Result: FAIL"
  exit 1
fi
echo ""
echo "============================================"
