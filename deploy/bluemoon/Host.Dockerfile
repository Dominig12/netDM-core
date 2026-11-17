# syntax=docker/dockerfile:1
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e
FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ src/
RUN dotnet publish src/NetDM.Host -c Release -r linux-x64 --self-contained -o /release

FROM debian:trixie-slim@sha256:a72b4433de1cf7d6bae0c4cee74d0547f58601a643b3375f070c51600d63b6b2
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl libicu76 libssl3t64 libstdc++6 zlib1g \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /netdm
COPY --from=build /release/ ./
USER 1654:1654
ENTRYPOINT ["/netdm/NetDM.Host"]
