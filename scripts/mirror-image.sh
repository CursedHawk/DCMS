#!/bin/sh
# Copies a third-party image into this project's registry under mirror/, so a deploy and CI pull
# it from infrastructure we run instead of from whoever publishes it upstream.
#
# Why: MinIO stopped publishing images. Its Docker Hub repositories went 401, then quay.io did
# too, and each time every deploy's `compose pull` and every Testcontainers fixture failed at
# once. An upstream can disappear, go private or rewrite a tag; a copy in our registry cannot.
#
#   ./scripts/mirror-image.sh <source-image> <name:tag>
#   ./scripts/mirror-image.sh quay.io/minio/mc:latest mc:RELEASE.2025-08-13T08-35-41Z
#
# The source may already be local (`docker save` from a host that still has it cached, then
# `docker load`) -- that is how the MinIO images were rescued. Otherwise it is copied registry to
# registry with `buildx imagetools create`, which never lands in the local image store (VPSM has
# no disk for a 1 GB SDK) -- linux/amd64 only, the one platform the runners and hosts run.
# Tag with the upstream VERSION, never `latest`: the tag is the only record of what it is.
# Needs `docker login registry-gitlab.highgeek.eu` with a token that can write the registry.
set -eu

[ $# -eq 2 ] || { echo "usage: $0 <source-image> <name:tag>" >&2; exit 2; }
src=$1
dst=registry-gitlab.highgeek.eu/cursedhawk/baas-dcms/mirror/$2
tag=${2##*:}
[ "$tag" != "$2" ] && [ "$tag" != latest ] || { echo "tag $2 with the upstream version, not latest" >&2; exit 2; }

if docker image inspect "$src" >/dev/null 2>&1; then
    docker tag "$src" "$dst"
    docker push "$dst"
else
    docker buildx imagetools create --platform linux/amd64 --tag "$dst" "$src"
fi
echo "mirrored: $dst"
