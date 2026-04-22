# syntax=docker/dockerfile:1.7
# Builder stage: publishes the runner as a self-contained single-file binary.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/YobaConf.Runner/YobaConf.Runner.csproj ./src/YobaConf.Runner/
RUN dotnet restore src/YobaConf.Runner/YobaConf.Runner.csproj -r linux-x64

COPY src/YobaConf.Runner/ ./src/YobaConf.Runner/
RUN dotnet publish src/YobaConf.Runner/YobaConf.Runner.csproj \
	-c Release \
	-r linux-x64 \
	--self-contained \
	--no-restore \
	-o /app/publish

# Runtime stage: the image's sole purpose is to expose `/yobaconf-run` so downstream
# consumers can `COPY --from=ghcr.io/stdray/yobaconf-runner /yobaconf-run /usr/local/bin/`.
# We keep the binary at the image root and do not set an ENTRYPOINT — dispatching the
# child command is the consumer's job.
FROM mcr.microsoft.com/dotnet/nightly/runtime-deps:10.0-noble-chiseled AS final
ARG APP_VERSION
ARG GIT_SHORT_SHA
ENV APP_VERSION=$APP_VERSION
ENV GIT_SHORT_SHA=$GIT_SHORT_SHA
WORKDIR /
COPY --from=build /app/publish/yobaconf-run /yobaconf-run
ENTRYPOINT ["/yobaconf-run"]
