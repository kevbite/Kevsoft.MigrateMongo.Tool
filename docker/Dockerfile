# Images for ghcr.io/kevbite/migrate-mongo, built from the prebuilt Native AOT binaries so no step runs under
# emulation. The build context needs bin/<amd64|arm64>/migrate-mongo (see the docker job in .github/workflows/ci.yml).
#
#   tool   (default)   the .NET 10 SDK plus the .NET 8 and 9 runtimes: builds and runs your migrations project.
#   bundle             runtime-deps only: drives a bundle (`up --runner ./bundle/Migrations`) on a machine without .NET.

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS bundle
ARG TARGETARCH
COPY --chmod=755 bin/${TARGETARCH}/migrate-mongo /usr/local/bin/migrate-mongo
WORKDIR /src
ENTRYPOINT ["migrate-mongo"]

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS tool
ARG TARGETARCH
# The tool runs your project on the runtime it targets, so add the older supported runtimes next to .NET 10's.
COPY --from=mcr.microsoft.com/dotnet/runtime:8.0 /usr/share/dotnet/shared/Microsoft.NETCore.App /usr/share/dotnet/shared/Microsoft.NETCore.App
COPY --from=mcr.microsoft.com/dotnet/runtime:9.0 /usr/share/dotnet/shared/Microsoft.NETCore.App /usr/share/dotnet/shared/Microsoft.NETCore.App
COPY --chmod=755 bin/${TARGETARCH}/migrate-mongo /usr/local/bin/migrate-mongo
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
WORKDIR /src
ENTRYPOINT ["migrate-mongo"]
