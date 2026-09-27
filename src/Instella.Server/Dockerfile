# Instella Server Dockerfile
# Minimal Alpine image using pre-built binaries

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app

# The .NET base image's non-root user "app" has a fixed uid/gid, 1654: host volumes can be
# prepared with chown -R 1654:1654, and the uid does not change between image versions.
RUN mkdir -p /config /packages && chown -R app:app /config /packages

# Copy pre-built application (from build script's publish output)
COPY publish/ .

# Set environment variables. The database, Data Protection keys and the first-run setup token
# live in /config; package blobs in /packages. TLS is expected to terminate at a reverse proxy:
# list it in ReverseProxy__KnownProxies__0 so X-Forwarded-For/-Proto are trusted.
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV INSTELLA_CONFIG_DIR=/config
ENV Storage__Local__BasePath=/packages
ENV Server__TlsTerminatedByProxy=true
VOLUME ["/config", "/packages"]

# Use non-root user
USER app

# Expose port
EXPOSE 8080

# Health check (Alpine has wget through busybox; curl is absent)
HEALTHCHECK --interval=30s --timeout=10s --start-period=10s --retries=3 \
    CMD wget -qO- http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "Instella.Server.dll"]
