# syntax=docker/dockerfile:1
FROM mariadb:11.4@sha256:d1891ede5a559f1086f55053bb8cc1569b0298338c4d72d71725a7f74f6ba620
COPY --from=game /SQL/tgstation_schema.sql /docker-entrypoint-initdb.d/10-base.sql
COPY --from=game /SQL/bluemoon_schema.sql /docker-entrypoint-initdb.d/20-bluemoon.sql
COPY --from=game /SQL/bluemoon_whitelist_schema.sql /docker-entrypoint-initdb.d/30-whitelist.sql
COPY --from=game /SQL/mentor.sql /docker-entrypoint-initdb.d/40-mentor.sql
COPY deploy/bluemoon/schema-revision.sql /docker-entrypoint-initdb.d/90-revision.sql
COPY --chmod=755 deploy/bluemoon/check-database.sh /usr/local/bin/bluemoon-check-db
