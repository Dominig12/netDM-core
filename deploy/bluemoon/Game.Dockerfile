# syntax=docker/dockerfile:1
ARG BYOND_IMAGE=ghcr.io/neurekadev/byond@sha256:3b3bcfc1499e27a2f098ac8a9d1ef450c4fc825ccd411763457ad53a82326c0a
ARG NODE_IMAGE=node:24.16.0-bookworm-slim@sha256:2c87ef9bd3c6a3bd4b472b4bec2ce9d16354b0c574f736c476489d09f560a203
FROM ${NODE_IMAGE} AS node
FROM ${BYOND_IMAGE} AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl util-linux libssl3t64:i386 zlib1g:i386 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=node /usr/local/bin/node /usr/local/bin/node

FROM runtime AS toolchain
RUN apt-get update && apt-get install -y --no-install-recommends cmake g++-multilib git make \
    && rm -rf /var/lib/apt/lists/*

# The bridge is independent of the DM build, so bridge changes reuse the compiled world.
FROM toolchain AS bridge
COPY native/byond /native
RUN cmake -S /native -B /bridge -DCMAKE_BUILD_TYPE=Release -DCMAKE_CXX_FLAGS=-m32 \
    && cmake --build /bridge -j2 \
    && ! ldd /bridge/netdm_byond.so | grep 'not found'

FROM toolchain AS build
WORKDIR /src
COPY --from=game / /src/
RUN --mount=type=cache,target=/root/.yarn/berry/cache LANG=C.UTF-8 LC_ALL=C.UTF-8 node tools/build/build.js build --ci --warnings-as-errors
RUN bash tools/deploy.sh /release \
    && cp -r config /release/config \
    && cp netdm-source.json LICENSE GPLv3.txt /release/ \
    && install -d /release/tgui/public /release/tgui/packages/tgfont/dist \
    && cp tgui/public/tgui.html tgui/public/*.bundle.js tgui/public/*.bundle.css /release/tgui/public/ \
    && cp tgui/packages/tgfont/dist/tgfont.* /release/tgui/packages/tgfont/dist/ \
    && cp libauxmos.so /release/ \
    && curl -fsSL --retry 3 https://github.com/tgstation/rust-g/releases/download/6.2.0/librust_g.so -o /release/librust_g.so \
    && echo 'a0d548808afd31c6ef3b53c01b3267e14086f02626111de8bd2b974682bb325b  /release/librust_g.so' | sha256sum -c - \
    && ! ldd /release/librust_g.so /release/libauxmos.so | grep 'not found'

FROM runtime
WORKDIR /game
COPY --from=build --chown=1654:1654 /release/ ./
COPY --from=bridge --chown=1654:1654 /bridge/netdm_byond.so ./
COPY --chmod=755 deploy/bluemoon/game-start.sh /usr/local/bin/bluemoon-start
RUN chown 1654:1654 /game
ENV HOME=/game LANG=C.UTF-8 LC_ALL=C.UTF-8
EXPOSE 1337/tcp
ENTRYPOINT ["/usr/local/bin/bluemoon-start"]
