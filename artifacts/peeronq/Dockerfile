FROM node:24.21.0-bookworm-slim@sha256:0e0ff40c39bc087845bfb27465a0df4ea419520094bc35842ff83dd8cbe6f9b6 AS build
WORKDIR /workspace
RUN corepack enable

COPY package.json pnpm-lock.yaml pnpm-workspace.yaml tsconfig.base.json ./
COPY scripts/check-package-manager.mjs scripts/check-package-manager.mjs
COPY artifacts/peeronq/package.json artifacts/peeronq/package.json
COPY lib/api-client-react/package.json lib/api-client-react/package.json
RUN pnpm install --frozen-lockfile --filter @workspace/peeronq...

COPY artifacts/peeronq artifacts/peeronq
COPY lib/api-client-react lib/api-client-react
ARG BASE_PATH=/
ARG VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL
ARG VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE=false
ARG VITE_PEERONQ_MACOS_DOWNLOAD_URL
ARG VITE_PEERONQ_LINUX_X64_DOWNLOAD_URL
ARG VITE_PEERONQ_LINUX_ARM64_DOWNLOAD_URL
ARG VITE_PEERONQ_ANDROID_DOWNLOAD_URL
ARG VITE_PEERONQ_IOS_DOWNLOAD_URL
ARG VITE_PEERONQ_ACCOUNT_PORTAL_URL=https://portal.peeronq.com
ENV BASE_PATH=${BASE_PATH}
ENV VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL=${VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL}
ENV VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE=${VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE}
ENV VITE_PEERONQ_MACOS_DOWNLOAD_URL=${VITE_PEERONQ_MACOS_DOWNLOAD_URL}
ENV VITE_PEERONQ_LINUX_X64_DOWNLOAD_URL=${VITE_PEERONQ_LINUX_X64_DOWNLOAD_URL}
ENV VITE_PEERONQ_LINUX_ARM64_DOWNLOAD_URL=${VITE_PEERONQ_LINUX_ARM64_DOWNLOAD_URL}
ENV VITE_PEERONQ_ANDROID_DOWNLOAD_URL=${VITE_PEERONQ_ANDROID_DOWNLOAD_URL}
ENV VITE_PEERONQ_IOS_DOWNLOAD_URL=${VITE_PEERONQ_IOS_DOWNLOAD_URL}
ENV VITE_PEERONQ_ACCOUNT_PORTAL_URL=${VITE_PEERONQ_ACCOUNT_PORTAL_URL}
RUN if [ -f /workspace/artifacts/peeronq/public/downloads/embedded-windows-version.txt ]; then \
      test -f /workspace/artifacts/peeronq/public/downloads/PeerOnQ-Windows-x64.msi; \
      test -f /workspace/artifacts/peeronq/public/downloads/SHA256SUMS.txt; \
      test -f /workspace/artifacts/peeronq/public/downloads/embedded-windows-release-type.txt; \
      expected_sha256="$(awk '$2 == "PeerOnQ-Windows-x64.msi" { print $1; exit }' /workspace/artifacts/peeronq/public/downloads/SHA256SUMS.txt)"; \
      actual_sha256="$(sha256sum /workspace/artifacts/peeronq/public/downloads/PeerOnQ-Windows-x64.msi | awk '{ print $1 }')"; \
      test -n "$expected_sha256" && test "$actual_sha256" = "$expected_sha256"; \
      release_type="$(cat /workspace/artifacts/peeronq/public/downloads/embedded-windows-release-type.txt)"; \
      case "$release_type" in \
        signed) test ! -e /workspace/artifacts/peeronq/public/downloads/UNSIGNED-PILOT-NOTICE.txt; export VITE_PEERONQ_EMBEDDED_WINDOWS_UNSIGNED_PILOT=false ;; \
        unsigned-pilot) test -f /workspace/artifacts/peeronq/public/downloads/UNSIGNED-PILOT-NOTICE.txt; export VITE_PEERONQ_EMBEDDED_WINDOWS_UNSIGNED_PILOT=true ;; \
        *) exit 1 ;; \
      esac; \
      export VITE_PEERONQ_EMBEDDED_WINDOWS_X64_URL=/downloads/PeerOnQ-Windows-x64.msi; \
      export VITE_PEERONQ_EMBEDDED_WINDOWS_VERSION="$(cat /workspace/artifacts/peeronq/public/downloads/embedded-windows-version.txt)"; \
    fi; \
    pnpm --filter @workspace/peeronq run build

FROM nginx:1.28.3-alpine@sha256:a8b39bd9cf0f83869a2162827a0caf6137ddf759d50a171451b335cecc87d236 AS runtime
RUN apk del nginx nginx-module-acme nginx-module-geoip nginx-module-image-filter \
      nginx-module-njs nginx-module-xslt curl \
    && apk add --no-cache nginx=1.28.3-r7 \
    && apk upgrade --no-cache \
    && rm -f /etc/nginx/conf.d/default.conf
COPY artifacts/peeronq/nginx-main.conf /etc/nginx/nginx.conf
COPY artifacts/peeronq/nginx.conf /etc/nginx/http.d/default.conf
COPY --from=build /workspace/artifacts/peeronq/dist/public /usr/share/nginx/html
USER nginx
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=5s --retries=3 \
  CMD wget --quiet --spider http://127.0.0.1:8080/health/live || exit 1
CMD ["nginx", "-g", "pid /tmp/nginx.pid; daemon off;"]
