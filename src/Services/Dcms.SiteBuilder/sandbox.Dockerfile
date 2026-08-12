# The ephemeral, per-build sandbox image for Mode B (untrusted React) site builds.
# The SiteBuilder service launches one throwaway container from this image per build
# phase with: a scrubbed env (no platform secrets), --cap-drop ALL,
# --security-opt no-new-privileges, a read-only rootfs, hard cpu/memory/pids limits,
# and — for the build phase — no network. Install runs with --ignore-scripts, so no
# package lifecycle script ever executes here.
FROM node:24-slim

# Pre-activate the package managers so the build phase (which has NO network) never
# needs corepack to fetch them. corepack ships with Node.
RUN corepack enable \
    && corepack prepare pnpm@11.6.0 --activate \
    && corepack prepare yarn@1.22.22 --activate

# umask 0 so files created under the bind-mounted /work by this (possibly
# different-uid) container are readable and removable by the builder service that
# collects dist/ and cleans up the throwaway work dir afterwards.
RUN printf '#!/bin/sh\numask 0000\nexec "$@"\n' > /usr/local/bin/dcms-entry \
    && chmod +x /usr/local/bin/dcms-entry

# Never run the untrusted build as root, even inside the sandbox.
USER node
WORKDIR /work
ENTRYPOINT ["/usr/local/bin/dcms-entry"]
