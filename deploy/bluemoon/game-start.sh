#!/bin/sh
set -eu
password=$(cat /run/secrets/db_password)
case "$password" in
    ''|*[!a-zA-Z0-9_-]*) echo 'Invalid DB password file. Use a base64url-style password.' >&2; exit 1 ;;
esac

# uutils install (Ubuntu 26.04) rejects numeric owners without passwd entries.
mkdir -p data
chmod 0750 data
chown 1654:1654 data
umask 077
dbconfig=$(mktemp config/entries/.dbconfig.XXXXXX)
trap 'rm -f "$dbconfig"' EXIT
printf 'SQL_ENABLED\nADDRESS db\nPORT 3306\nFEEDBACK_DATABASE bluemoon\nFEEDBACK_DATABASE_WHITELIST bluemoon\nFEEDBACK_LOGIN bluemoon\nFEEDBACK_PASSWORD %s\n' "$password" >"$dbconfig"
chown 1654:1654 "$dbconfig"
mv -f "$dbconfig" config/entries/dbconfig.txt
trap - EXIT
unset password
export NETDM_BUNDLE_STOP_FILE=data/.netdm-stop
rm -f "$NETDM_BUNDLE_STOP_FILE"
setpriv --reuid=1654 --regid=1654 --clear-groups --no-new-privs DreamDaemon tgstation.dmb -ports 1337 -trusted -close -invisible &
daemon=$!
stop_world() {
    trap '' TERM INT
    printf 'stop\n' >"$NETDM_BUNDLE_STOP_FILE"
    wait "$daemon"
    exit "$?"
}
trap stop_world TERM INT
wait "$daemon"
