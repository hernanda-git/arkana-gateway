FROM golang:1.26-bookworm@sha256:9fdc884aacc3bec89b20ffc69f4bb369c78210e3e4f600387b5128b12c199f81 AS builder

ARG UPSTREAM_COMMIT=d31b15916d15b550bbf388fd6da4a47d4d864109
ARG VERSION=7.2.146-antigravity-https
ARG BUILD_DATE=unknown
ARG DEBIAN_SNAPSHOT=20260917T000000Z

WORKDIR /app
RUN rm -f /etc/apt/sources.list /etc/apt/sources.list.d/debian.sources \
    && printf '%s\n' \
       "deb [check-valid-until=no] http://snapshot.debian.org/archive/debian/${DEBIAN_SNAPSHOT}/ bookworm main" \
       > /etc/apt/sources.list \
    && apt-get -o Acquire::Check-Valid-Until=false update \
    && apt-get install -y --no-install-recommends build-essential git ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && git init . \
    && git remote add origin https://github.com/router-for-me/CLIProxyAPI.git \
    && git fetch --depth 1 origin "${UPSTREAM_COMMIT}" \
    && git checkout --detach FETCH_HEAD \
    && test "$(git rev-parse HEAD)" = "${UPSTREAM_COMMIT}"

COPY antigravity-https-callback.patch /tmp/antigravity-https-callback.patch
RUN tr -d '\r' < /tmp/antigravity-https-callback.patch > /tmp/antigravity-https-callback.patch.unix \
    && mv /tmp/antigravity-https-callback.patch.unix /tmp/antigravity-https-callback.patch \
    && git apply /tmp/antigravity-https-callback.patch \
    && gofmt -w internal/auth/antigravity/auth.go internal/api/handlers/management/auth_files_provider_oauth.go \
    && go mod download \
    && go mod verify

RUN CGO_ENABLED=1 GOOS=linux go build -mod=readonly -trimpath -buildvcs=false \
    -ldflags="-s -w -X 'main.Version=${VERSION}' -X 'main.Commit=${UPSTREAM_COMMIT}' -X 'main.BuildDate=${BUILD_DATE}'" \
    -o ./CLIProxyAPI ./cmd/server/

FROM debian:bookworm@sha256:6ebd97fa83deb272194a2cf015b3d26a4d538e9ad3a7a79d544c8af5b0a01443

ARG TZ=Asia/Shanghai
ARG DEBIAN_SNAPSHOT=20260917T000000Z
RUN rm -f /etc/apt/sources.list /etc/apt/sources.list.d/debian.sources \
    && printf '%s\n' \
       "deb [check-valid-until=no] http://snapshot.debian.org/archive/debian/${DEBIAN_SNAPSHOT}/ bookworm main" \
       > /etc/apt/sources.list \
    && apt-get -o Acquire::Check-Valid-Until=false update \
    && apt-get install -y --no-install-recommends tzdata ca-certificates curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /CLIProxyAPI \
    && cp "/usr/share/zoneinfo/${TZ}" /etc/localtime \
    && echo "${TZ}" > /etc/timezone

COPY --from=builder /app/CLIProxyAPI /CLIProxyAPI/CLIProxyAPI
COPY --from=builder /app/config.example.yaml /CLIProxyAPI/config.example.yaml

WORKDIR /CLIProxyAPI
EXPOSE 8317
ENV TZ=${TZ}
CMD ["./CLIProxyAPI"]
