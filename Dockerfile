# RuleForge runtime engine — multi-stage build, framework-dependent runtime.
# Final image is ~210 MB; build cached intermediate is ~900 MB.

# ─── build ──────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj files first to maximise layer cache hits on dependency restore.
COPY RuleForge.slnx ./
COPY src/RuleForge.Api/RuleForge.Api.csproj                       src/RuleForge.Api/
COPY src/RuleForge.Cli/RuleForge.Cli.csproj                       src/RuleForge.Cli/
COPY src/RuleForge.Core/RuleForge.Core.csproj                     src/RuleForge.Core/
COPY src/RuleForge.DocumentForge/RuleForge.DocumentForge.csproj   src/RuleForge.DocumentForge/
RUN dotnet restore src/RuleForge.Api/RuleForge.Api.csproj

# Copy the rest of the source and publish.
COPY src/ src/
RUN dotnet publish src/RuleForge.Api/RuleForge.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /out

# ─── runtime ────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /out ./

# Bundle the local fixture pack so RULEFORGE_RULE_SOURCE=local works out of
# the box for smoke testing without DocumentForge.
COPY fixtures ./fixtures

# Bind to all interfaces on a single port. Render's PaaS routes its public
# port to this; for local docker run, expose 8080 → host port of choice.
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_USE_POLLING_FILE_WATCHER=false
ENV DOTNET_GCHeapHardLimitPercent=80

ENTRYPOINT ["dotnet", "RuleForge.Api.dll"]
