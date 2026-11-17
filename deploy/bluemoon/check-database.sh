#!/bin/sh
set -eu
export MYSQL_PWD="$(cat /run/secrets/db_password)"
revision=$(mariadb --host=db --protocol=TCP --user=bluemoon --database=bluemoon --batch --skip-column-names \
    --execute='SELECT CONCAT(major, ".", minor) FROM schema_revision ORDER BY date DESC LIMIT 1')
test "$revision" = 4.7
mariadb --host=db --protocol=TCP --user=bluemoon --database=bluemoon --batch --skip-column-names \
    --execute='SELECT COUNT(*) FROM round; SELECT COUNT(*) FROM player_rank; SELECT COUNT(*) FROM whitelist_log' >/dev/null
echo "BlueMoon database schema $revision ready"
