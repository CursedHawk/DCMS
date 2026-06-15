#!/bin/sh
# Creates the per-concern buckets (tenant isolation happens via key prefixes,
# see ADR: bucket-per-concern). Idempotent.
set -e

mc alias set local "${MINIO_URL:-http://minio:9000}" "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD"

for bucket in dcms-media dcms-sites dcms-build-logs; do
  if mc ls "local/$bucket" >/dev/null 2>&1; then
    echo "bucket $bucket exists"
  else
    mc mb "local/$bucket"
    echo "bucket $bucket created"
  fi
done

echo "MinIO provisioning complete."
