FROM node:24.4.1-alpine@sha256:820e86612c21d0636580206d802a726f2595366e1b867e564cbc652024151e8a AS build
WORKDIR /workspace
RUN corepack enable
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY scripts/check-package-manager.mjs scripts/check-package-manager.mjs
COPY artifacts/peeronq-portal/package.json artifacts/peeronq-portal/package.json
RUN pnpm install --frozen-lockfile --filter @workspace/peeronq-portal...
COPY artifacts/peeronq-portal artifacts/peeronq-portal
RUN pnpm --filter @workspace/peeronq-portal run build

FROM nginx:1.28.3-alpine@sha256:a8b39bd9cf0f83869a2162827a0caf6137ddf759d50a171451b335cecc87d236 AS runtime
# The immutable upstream digest already contains the matching nginx runtime.
# Avoid a network-dependent package reinstall: Alpine may retire the exact
# revision while this reproducible image remains valid and pinned.
RUN apk del curl \
    && sed -i 's#pid .*nginx.pid;#pid /tmp/nginx.pid;#' /etc/nginx/nginx.conf \
    && rm -f /etc/nginx/conf.d/default.conf
COPY artifacts/peeronq-portal/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /workspace/artifacts/peeronq-portal/dist /usr/share/nginx/html
USER nginx
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=5s --retries=3 CMD wget --quiet --spider http://127.0.0.1:8080/health/live || exit 1
CMD ["nginx", "-g", "daemon off;"]
