FROM node:24-alpine AS build
RUN npm install -g pnpm
WORKDIR /repo
# No VITE_* build args, deliberately: Vite substitutes import.meta.env at BUILD time, which
# would make this image environment-specific and defeat promoting a tested digest. The
# per-environment values are substituted into index.html at container start by
# docker-entrypoint.sh. See apps/platform/src/runtime-config.ts.
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml ./
COPY apps/ apps/
COPY packages/ packages/
# Plugins' admin UIs are workspace packages beside their C# (docs/adr/0019); the lockfile names
# them, so the install needs them even where the app does not compile them in.
COPY src/Plugins/ src/Plugins/
COPY samples/ samples/
RUN pnpm install --frozen-lockfile && pnpm --filter @dcms/platform build

FROM nginx:alpine AS runtime
COPY apps/platform/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /repo/apps/platform/dist /usr/share/nginx/html
COPY apps/platform/docker-entrypoint.sh /docker-entrypoint.d/00-dcms-runtime-config.sh
RUN chmod +x /docker-entrypoint.d/00-dcms-runtime-config.sh
EXPOSE 80
