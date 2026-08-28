FROM node:24-alpine AS build
RUN npm install -g pnpm
WORKDIR /repo
# No VITE_OIDC_AUTHORITY build arg any more, deliberately.
#
# Vite substitutes import.meta.env at BUILD time, so baking the authority in produced an
# environment-SPECIFIC image: the artifact built for dev could not serve production, which
# makes "promote the tested digest" impossible for the one thing users actually load. The
# per-environment values are now substituted into index.html at container start by
# docker-entrypoint.sh. See apps/admin/src/runtime-config.ts.
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml ./
COPY apps/ apps/
COPY packages/ packages/
RUN pnpm install --frozen-lockfile && pnpm --filter @dcms/admin build

FROM nginx:alpine AS runtime
COPY apps/admin/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /repo/apps/admin/dist /usr/share/nginx/html
COPY apps/admin/docker-entrypoint.sh /docker-entrypoint.d/00-dcms-runtime-config.sh
RUN chmod +x /docker-entrypoint.d/00-dcms-runtime-config.sh
EXPOSE 80
