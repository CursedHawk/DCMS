#!/bin/sh
# Creates the per-concern buckets (tenant isolation happens via key prefixes,
# see ADR: bucket-per-concern) and a least-privilege service account for the
# site-builder scoped to just the site buckets. Idempotent.
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

# ---- Least-privilege site-builder credentials -----------------------------
# The site-builder must never hold the MinIO root key (which can read/write every
# tenant's data in every bucket). Give it a service account whose policy is scoped
# to only the site buckets it uses (dcms-sites for artifacts + logs; dcms-build-logs
# reserved). A leaked builder key then cannot touch dcms-media or anything else.
SB_USER="${SITEBUILDER_MINIO_USER:-dcms-sitebuilder}"
SB_PASS="${SITEBUILDER_MINIO_PASSWORD:-dcms-sitebuilder-dev}"

cat > /tmp/dcms-sitebuilder-policy.json <<'JSON'
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"],
      "Resource": [
        "arn:aws:s3:::dcms-sites/*",
        "arn:aws:s3:::dcms-build-logs/*"
      ]
    },
    {
      "Effect": "Allow",
      "Action": ["s3:ListBucket", "s3:GetBucketLocation"],
      "Resource": [
        "arn:aws:s3:::dcms-sites",
        "arn:aws:s3:::dcms-build-logs"
      ]
    }
  ]
}
JSON

# `mc admin policy create` is the current verb (older mc used `add`); tolerate both
# and re-runs.
mc admin policy create local dcms-sitebuilder /tmp/dcms-sitebuilder-policy.json 2>/dev/null \
  || mc admin policy add local dcms-sitebuilder /tmp/dcms-sitebuilder-policy.json 2>/dev/null \
  || echo "policy dcms-sitebuilder already exists"

mc admin user add local "$SB_USER" "$SB_PASS" 2>/dev/null \
  || echo "user $SB_USER already exists"

mc admin policy attach local dcms-sitebuilder --user "$SB_USER" 2>/dev/null \
  || mc admin policy set local dcms-sitebuilder user="$SB_USER" 2>/dev/null \
  || echo "policy dcms-sitebuilder already attached to $SB_USER"

rm -f /tmp/dcms-sitebuilder-policy.json

echo "MinIO provisioning complete."
