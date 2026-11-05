#!/usr/bin/env bash
set -euo pipefail

base="http://127.0.0.1:5087"
log_file="${RUNNER_TEMP:-/tmp}/netdm-host.log"
dotnet run --project src/NetDM.Host --configuration Release --no-build >"$log_file" 2>&1 &
host_pid=$!
cleanup() {
  kill "$host_pid" 2>/dev/null || true
  wait "$host_pid" 2>/dev/null || true
}
trap cleanup EXIT
trap 'cat "$log_file" >&2' ERR

for i in $(seq 1 30); do
  if curl -fsS "$base/api/health" >/dev/null 2>&1; then break; fi
  sleep 1
done

curl -fsS "$base/api/health" | python3 -c 'import json,sys; assert json.load(sys.stdin)["status"]=="ok"'
curl -fsS "$base/" | grep -q 'Network Observatory'
curl -fsS "$base/app.js" | grep -q 'fitMap'
node --check src/NetDM.Host/wwwroot/app.js


# Run an isolated comparison before starting the live workload. It validates every
# alternative route against baseline cost and verifies the resulting path in-core.
curl -fsS -X POST "$base/api/comparison/start" \
  -H 'Content-Type: application/json' \
  -d '{"nodes":160,"routes":20,"viewportQueries":20,"moveBatches":4,"seed":7331}' >/dev/null
for i in $(seq 1 25); do
  curl -fsS "$base/api/comparison" > "${RUNNER_TEMP:-/tmp}/netdm-comparison.json"
  status="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["status"])' "${RUNNER_TEMP:-/tmp}/netdm-comparison.json")"
  if [[ "$status" == "complete" ]]; then break; fi
  if [[ "$status" == "failed" ]]; then
    cat "${RUNNER_TEMP:-/tmp}/netdm-comparison.json" >&2
    exit 1
  fi
  sleep 1
done
python3 - <<'PY'
import json, os
with open(os.path.join(os.environ.get("RUNNER_TEMP", "/tmp"),"netdm-comparison.json"),encoding="utf-8") as f:
    state=json.load(f)
assert state["status"] == "complete", state
report=state["report"]
assert report["nodes"] == 160 and report["undirectedEdges"] >= 159, report
assert len(report["algorithms"]) == 4, report
assert all(x["routes"] == 20 and x["p95Us"] >= 0 for x in report["algorithms"]), report
assert report["csrArrayPayloadBytes"] > 0 and report["csrBuildMs"] >= 0, report
assert report["spatial"]["queries"] == 20, report
assert report["movement"]["batches"] == 4, report
print("Cold-cache routing + spatial + CSR comparison: PASS")
PY

curl -fsS -X POST "$base/api/benchmark/start" \
  -H 'Content-Type: application/json' \
  -d '{"nodes":100,"routesPerSecond":40,"movesPerSecond":20,"linkChangesPerSecond":2,"durationSeconds":9,"seed":1337}' \
  >/dev/null
sleep 4

curl -fsS "$base/api/metrics" > "${RUNNER_TEMP:-/tmp}/netdm-metrics.json"
curl -fsS "$base/api/map?minX=-10&minY=-10&maxX=150&maxY=150&limit=1000" > "${RUNNER_TEMP:-/tmp}/netdm-map.json"
curl -fsS "$base/api/history" > "${RUNNER_TEMP:-/tmp}/netdm-history.json"

python3 - <<'PY'
import json, os
directory = os.environ.get("RUNNER_TEMP", "/tmp")
def load(name):
    with open(os.path.join(directory, name), encoding="utf-8") as f:
        return json.load(f)
m = load("netdm-metrics.json")
v = load("netdm-map.json")
h = load("netdm-history.json")
assert m["nodeCount"] == 100, m
assert m["edgeCount"] >= 99, m
assert m["runtime"]["routes"] > 0, m
assert m["runtime"]["moves"] > 0, m
assert m["connectivityVersion"] > 0, m
assert v["totalNodes"] == 100 and v["displayedNodes"] > 0, v
assert len(v["edges"]) > 0, v
assert isinstance(v["nodes"][0]["id"], str), v
assert len(h) > 0 and h[-1]["actualRoutesPerSecond"] >= 0, h
print("Host HTTP + benchmark + visualizer integration: PASS")
PY
curl -fsS -X POST "$base/api/benchmark/stop" >/dev/null
