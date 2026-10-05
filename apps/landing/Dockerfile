FROM node:24-alpine AS build
RUN npm install -g pnpm
WORKDIR /repo
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml ./
COPY apps/ apps/
COPY packages/ packages/
# Plugins' admin UIs are workspace packages the lockfile names, so the install needs them.
COPY src/Plugins/ src/Plugins/
COPY samples/ samples/
# The build also prerenders every page and fails if one is missing its <h1>, title or canonical.
RUN pnpm install --frozen-lockfile && pnpm --filter @dcms/landing build

FROM nginx:alpine AS runtime
COPY apps/landing/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /repo/apps/landing/dist /usr/share/nginx/html
EXPOSE 80
