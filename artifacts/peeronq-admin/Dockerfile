FROM node:24.4.1-alpine@sha256:820e86612c21d0636580206d802a726f2595366e1b867e564cbc652024151e8a AS build
WORKDIR /workspace
RUN corepack enable

COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY scripts/check-package-manager.mjs scripts/check-package-manager.mjs
COPY artifacts/peeronq-admin/package.json artifacts/peeronq-admin/package.json
RUN pnpm install --frozen-lockfile --filter @workspace/peeronq-admin...

COPY artifacts/peeronq-admin artifacts/peeronq-admin
RUN pnpm --filter @workspace/peeronq-admin run build

FROM nginx:1.28.3-alpine@sha256:a8b39bd9cf0f83869a2162827a0caf6137ddf759d50a171451b335cecc87d236 AS runtime
RUN apk del nginx nginx-module-acme nginx-module-geoip nginx-module-image-filter \
      nginx-module-njs nginx-module-xslt curl \
    && apk add --no-cache nginx=1.28.3-r7 \
    && apk upgrade --no-cache \
    && rm -f /etc/nginx/conf.d/default.conf
COPY artifacts/peeronq-admin/nginx.conf /etc/nginx/http.d/default.conf
COPY --from=build /workspace/artifacts/peeronq-admin/dist /usr/share/nginx/html
USER nginx
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=5s --retries=3 \
  CMD wget --quiet --spider http://127.0.0.1:8080/health/live || exit 1
CMD ["nginx", "-g", "pid /tmp/nginx.pid; daemon off;"]
