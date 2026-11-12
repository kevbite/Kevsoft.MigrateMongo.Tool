# Images for ghcr.io/kevbite/migrate-mongo, built from the prebuilt Native AOT binaries so no step runs under
# emulation. The build context needs bin/<amd64|arm64>/migrate-mongo (see the docker job in .github/workflows/ci.yml).
#
#   tool   (default)   the .NET 11 SDK plus the .NET 10 runtime: builds your migrations project on any supported
#                      target framework (net10.0 the floor, net11.0) and runs it.
#   bundle             runtime-deps only: drives a bundle (`up --runner ./bundle/Migrations`) on a machine without .NET.

FROM mcr.microsoft.com/dotnet/runtime-deps:11.0 AS bundle
ARG TARGETARCH
COPY --chmod=755 bin/${TARGETARCH}/migrate-mongo /usr/local/bin/migrate-mongo
WORKDIR /src
ENTRYPOINT ["migrate-mongo"]

FROM mcr.microsoft.com/dotnet/sdk:11.0-resolute AS tool
# The 11 SDK can build any supported target, but ships only the 11 runtime. Add the 10 runtime (the floor) so the
# image also runs net10.0 output. Both images are resolute (Ubuntu 26.04) so the copied runtime matches the SDK's glibc.
COPY --from=mcr.microsoft.com/dotnet/aspnet:10.0-resolute /usr/share/dotnet/shared /usr/share/dotnet/shared
ARG TARGETARCH
COPY --chmod=755 bin/${TARGETARCH}/migrate-mongo /usr/local/bin/migrate-mongo
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
WORKDIR /src
ENTRYPOINT ["migrate-mongo"]
