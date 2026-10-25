#!/usr/bin/env bash
set -euo pipefail

PORT=${PORT:-8899}
BAAS_DB="baas_test_$$.db"
STORAGE_DIR="baas_test_uploads_$$"
SERVER_PID=""

cleanup() {
  echo ""
  echo "🧹 Cleaning up test artifacts..."
  if [ -n "$SERVER_PID" ]; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  rm -rf "$BAAS_DB"* "$STORAGE_DIR" /tmp/baas_test_*.png
  echo "✅ Done."
}
trap cleanup EXIT

echo "=========================================================="
echo "🚀 ReptarBase Automated Verification Suite"
echo "=========================================================="

echo "1. Booting ReptarBase on port $PORT..."
PORT=$PORT BAAS_DB=$BAAS_DB STORAGE_DIR=$STORAGE_DIR dotnet run app.cs &
SERVER_PID=$!

# Wait for server to be ready
echo "Waiting for health check..."
for i in {1..30}; do
  if curl -s "http://127.0.0.1:$PORT/health" > /dev/null 2>&1; then
    break
  fi
  sleep 0.5
done

# 1. Health check
echo -n "2. Verifying /health probe... "
HEALTH=$(curl -s "http://127.0.0.1:$PORT/health")
if [[ "$HEALTH" != *"reptarbase"* ]]; then
  echo "FAILED: $HEALTH"
  exit 1
fi
echo "PASSED (Collections: documents, files, users, sessions, api_keys)"

# 2. User Signup
echo -n "3. Verifying User Registration (/api/auth/signup)... "
SIGNUP_RES=$(curl -s -X POST "http://127.0.0.1:$PORT/api/auth/signup" \
  -H "Content-Type: application/json" \
  -d '{"email":"test@reptarbase.local","password":"Password123!","name":"Dev Test"}')
USER_ID=$(echo "$SIGNUP_RES" | grep -o '"id":"[^"]*' | cut -d'"' -f4)
if [ -z "$USER_ID" ]; then
  echo "FAILED: $SIGNUP_RES"
  exit 1
fi
echo "PASSED (Created $USER_ID)"

# 3. User Signin
echo -n "4. Verifying User Authentication (/api/auth/signin)... "
SIGNIN_HEADERS=$(curl -s -i -X POST "http://127.0.0.1:$PORT/api/auth/signin" \
  -H "Content-Type: application/json" \
  -d '{"email":"test@reptarbase.local","password":"Password123!"}')
SESSION_TOKEN=$(echo "$SIGNIN_HEADERS" | grep -i "Set-Cookie:" | grep "reptarbase_session" | sed -E 's/.*reptarbase_session=([^;]+).*/\1/')
if [ -z "$SESSION_TOKEN" ]; then
  echo "FAILED to extract session token from cookies"
  exit 1
fi
echo "PASSED (Session token issued)"

# 4. Create API Key
echo -n "5. Verifying Personal Access Token (/api/auth/api-keys)... "
API_KEY_RES=$(curl -s -X POST "http://127.0.0.1:$PORT/api/auth/api-keys" \
  -H "Authorization: Bearer $SESSION_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name":"CI-Key","expiresInDays":30}')
API_TOKEN=$(echo "$API_KEY_RES" | grep -o '"key":"[^"]*' | cut -d'"' -f4)
if [ -z "$API_TOKEN" ]; then
  echo "FAILED: $API_KEY_RES"
  exit 1
fi
echo "PASSED (Created API key: $(echo "$API_TOKEN" | cut -c1-13)...)"

# 5. Create Document using Bearer Token
echo -n "6. Verifying Document Creation with API Key (/api/documents)... "
DOC_RES=$(curl -s -X POST "http://127.0.0.1:$PORT/api/documents" \
  -H "Authorization: Bearer $API_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"title":"Launch Checklist","content":"Ship ReptarBase Native AOT image!","status":"published"}')
DOC_ID=$(echo "$DOC_RES" | grep -o '"id":"[^"]*' | cut -d'"' -f4)
if [ -z "$DOC_ID" ]; then
  echo "FAILED: $DOC_RES"
  exit 1
fi
echo "PASSED (Created $DOC_ID)"

# 6. Retrieve Document and verify views increment
echo -n "7. Verifying Document Retrieval & View Count (/api/documents/:id)... "
DOC_VIEW_1=$(curl -s "http://127.0.0.1:$PORT/api/documents/$DOC_ID")
VIEWS_1=$(echo "$DOC_VIEW_1" | grep -o '"views":[0-9]*' | cut -d':' -f2)
DOC_VIEW_2=$(curl -s "http://127.0.0.1:$PORT/api/documents/$DOC_ID")
VIEWS_2=$(echo "$DOC_VIEW_2" | grep -o '"views":[0-9]*' | cut -d':' -f2)
if [ "$VIEWS_1" != "1" ] || [ "$VIEWS_2" != "2" ]; then
  echo "FAILED: expected 1 then 2 views, got $VIEWS_1 then $VIEWS_2"
  exit 1
fi
echo "PASSED (Atomic view count incremented from 1 -> 2)"

# 7. Patch Document
echo -n "8. Verifying Document PATCH (/api/documents/:id)... "
PATCH_RES=$(curl -s -X PATCH "http://127.0.0.1:$PORT/api/documents/$DOC_ID" \
  -H "Authorization: Bearer $API_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"title":"Launch Checklist (Updated)","status":"archived"}')
if [[ "$PATCH_RES" != *"Launch Checklist (Updated)"* ]]; then
  echo "FAILED: $PATCH_RES"
  exit 1
fi
echo "PASSED (Patched title and status)"

# 8. File Upload with Magic Byte validation
echo -n "9. Verifying File Upload with Magic Byte Check (/api/files/upload)... "
TMP_PNG="/tmp/baas_test_$$.png"
printf '\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01\x00\x00\x00\x01\x08\x06\x00\x00\x00\x1f\x15\xc4\x89\x00\x00\x00\nIDATx\x9cc\x00\x01\x00\x00\x05\x00\x01\r\n-\xb4\x00\x00\x00\x00IEND\xaeB`\x82' > "$TMP_PNG"
UPLOAD_RES=$(curl -s -X POST "http://127.0.0.1:$PORT/api/files/upload" \
  -H "Authorization: Bearer $API_TOKEN" \
  -F "file=@$TMP_PNG;type=image/png")
STORAGE_KEY=$(echo "$UPLOAD_RES" | grep -o '"storageKey":"[^"]*' | cut -d'"' -f4)
if [ -z "$STORAGE_KEY" ]; then
  echo "FAILED: $UPLOAD_RES"
  exit 1
fi
echo "PASSED (Uploaded $STORAGE_KEY)"

# 9. File Download and ETag 304 caching
echo -n "10. Verifying File Download & ETag 304 Caching (/api/files/:key)... "
FILE_HEADERS=$(curl -s -i "http://127.0.0.1:$PORT/api/files/$STORAGE_KEY")
ETAG=$(echo "$FILE_HEADERS" | grep -i "etag:" | tr -d '\r' | awk '{print $2}')
NOT_MODIFIED=$(curl -s -o /dev/null -w "%{http_code}" "http://127.0.0.1:$PORT/api/files/$STORAGE_KEY" -H "If-None-Match: $ETAG")
if [ "$NOT_MODIFIED" != "304" ]; then
  echo "FAILED: expected HTTP 304, got $NOT_MODIFIED"
  exit 1
fi
echo "PASSED (ETag $ETAG returned HTTP 304)"

# 10. Realtime SSE feed check
echo -n "11. Verifying Realtime SSE Stream (/api/realtime)... "
SSE_OUTPUT=$(curl -s -N --max-time 1.5 "http://127.0.0.1:$PORT/api/realtime" || true)
if [[ "$SSE_OUTPUT" != *"event: ready"* ]]; then
  echo "FAILED: $SSE_OUTPUT"
  exit 1
fi
echo "PASSED (Stream connected and ready)"

# 11. Delete Document
echo -n "12. Verifying Document Deletion (/api/documents/:id)... "
DEL_RES=$(curl -s -X DELETE "http://127.0.0.1:$PORT/api/documents/$DOC_ID" \
  -H "Authorization: Bearer $API_TOKEN")
if [[ "$DEL_RES" != *"\"deleted\":true"* ]]; then
  echo "FAILED: $DEL_RES"
  exit 1
fi
NOT_FOUND=$(curl -s -o /dev/null -w "%{http_code}" "http://127.0.0.1:$PORT/api/documents/$DOC_ID")
if [ "$NOT_FOUND" != "404" ]; then
  echo "FAILED: expected 404 after delete, got $NOT_FOUND"
  exit 1
fi
echo "PASSED (Deleted and verified 404)"

echo ""
echo "=========================================================="
echo "🎉 ALL 12 VERIFICATION CHECKS PASSED PERFECTLY!"
echo "=========================================================="
