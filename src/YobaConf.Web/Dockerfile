# syntax=docker/dockerfile:1.7
# Build stage: .NET SDK + bun for the MSBuild BuildFrontend target (Release-only, runs
# `bun install --frozen-lockfile && bun run build` inside src/YobaConf.Web). curl + unzip
# needed by the bun installer; apt lists removed after to keep the build image slim.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update \
	&& apt-get install -y --no-install-recommends curl unzip \
	&& rm -rf /var/lib/apt/lists/*
RUN curl -fsSL https://bun.sh/install | bash
ENV PATH="/root/.bun/bin:$PATH"

WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/YobaConf.Core/YobaConf.Core.csproj ./src/YobaConf.Core/
COPY src/YobaConf.Web/YobaConf.Web.csproj ./src/YobaConf.Web/
COPY src/YobaConf.Web/package.json src/YobaConf.Web/bun.lock ./src/YobaConf.Web/
RUN dotnet restore src/YobaConf.Web/YobaConf.Web.csproj -r linux-x64

COPY src/YobaConf.Core/ ./src/YobaConf.Core/
COPY src/YobaConf.Web/ ./src/YobaConf.Web/
RUN dotnet publish src/YobaConf.Web/YobaConf.Web.csproj \
	-c Release \
	-r linux-x64 \
	--self-contained \
	--no-restore \
	-o /app/publish

# Chiseled runtime runs as the `app` user (UID 1654). `dotnet publish` leaves /app/publish
# root-owned; if we COPY it as-is the app can't write to `data/` on startup ("unable to open
# database file" from SQLite). Pre-create the data dir in build stage; `COPY --chown` in the
# final stage hands the whole tree over to `app:app`.
RUN mkdir -p /app/publish/data

# Runtime stage: chiseled Ubuntu Noble (~15MB, no shell, no package manager).
# linq2db.SQLite.MS bundles e_sqlite3.so which is glibc-compatible so chiseled works.
# Shell-less runtime is validated by the DockerSmoke Cake task (`build.cake`).
FROM mcr.microsoft.com/dotnet/nightly/runtime-deps:10.0-noble-chiseled AS final
ARG APP_VERSION
ARG GIT_SHORT_SHA
ARG GIT_COMMIT_DATE
ENV APP_VERSION=$APP_VERSION
ENV GIT_SHORT_SHA=$GIT_SHORT_SHA
ENV GIT_COMMIT_DATE=$GIT_COMMIT_DATE
ENV ASPNETCORE_URLS=http://+:8080
WORKDIR /app
COPY --from=build --chown=app:app /app/publish .
# `data/` holds the SQLite file (Nodes / Variables / Secrets / ApiKeys / AuditLog).
# In prod mount a host volume here: `docker run ... -v /opt/yobaconf/data:/app/data ...`
# so the config tree and audit history survive restarts.
VOLUME ["/app/data"]
EXPOSE 8080
ENTRYPOINT ["./YobaConf.Web"]
