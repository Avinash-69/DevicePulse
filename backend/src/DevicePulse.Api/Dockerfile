# Multi-stage build: the SDK image compiles, the much smaller ASP.NET runtime image ships.
# Shipping the SDK would multiply the image size for no runtime benefit.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files are copied and restored before the rest of the source, so the restore layer is
# cached and only re-runs when a dependency actually changes — not on every code edit.
COPY ["src/DevicePulse.Api/DevicePulse.Api.csproj", "src/DevicePulse.Api/"]
RUN dotnet restore "src/DevicePulse.Api/DevicePulse.Api.csproj"

COPY . .
WORKDIR /src/src/DevicePulse.Api
RUN dotnet publish "DevicePulse.Api.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# A non-root user, because a container process that does not need root should not have it.
# The runtime images ship with 'app' (uid 1654) already created.
USER app

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=1

# Points at /health/ready, not /health/live: readiness checks the database too, which is what
# "is this container able to serve traffic" actually means. Liveness deliberately excludes it,
# so a database outage does not cause an endless restart loop.
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD ["/bin/sh", "-c", "wget -qO- http://127.0.0.1:8080/health/ready > /dev/null || exit 1"]

ENTRYPOINT ["dotnet", "DevicePulse.Api.dll"]
