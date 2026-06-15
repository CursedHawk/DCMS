FROM node:24-alpine AS build
RUN npm install -g pnpm
WORKDIR /repo
# Baked into the static bundle by Vite at build time.
ARG VITE_OIDC_AUTHORITY
ENV VITE_OIDC_AUTHORITY=$VITE_OIDC_AUTHORITY
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml ./
COPY apps/ apps/
COPY packages/ packages/
RUN pnpm install --frozen-lockfile && pnpm --filter @dcms/admin build

FROM nginx:alpine AS runtime
COPY apps/admin/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /repo/apps/admin/dist /usr/share/nginx/html
EXPOSE 80
