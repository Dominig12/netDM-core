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
curl -fsS "$base/" > "${RUNNER_TEMP:-/tmp}/netdm-index.html"
grep -q 'Network Observatory' "${RUNNER_TEMP:-/tmp}/netdm-index.html"
grep -q 'CPU Work Scheduler' "${RUNNER_TEMP:-/tmp}/netdm-index.html"
curl -fsS "$base/app.js" > "${RUNNER_TEMP:-/tmp}/netdm-app.js"
grep -q 'fitMap' "${RUNNER_TEMP:-/tmp}/netdm-app.js"
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

# Run a second scenario: radio links follow motion and temporary outages.
# This is a real DynamicRadioNetwork, not a cosmetic map animation.
for i in $(seq 1 20); do
  state="$(curl -fsS "$base/api/metrics" | python3 -c 'import json,sys;print(json.load(sys.stdin)["benchmark"]["status"])')"
  if [[ "$state" == "stopped" || "$state" == "complete" ]]; then break; fi
  sleep 1
done
curl -fsS -X POST "$base/api/benchmark/start" \
  -H 'Content-Type: application/json' \
  -d '{"nodes":100,"routesPerSecond":40,"movesPerSecond":90,"linkChangesPerSecond":40,"durationSeconds":9,"seed":2601,"wirelessEnabled":true,"radioRange":14}' >/dev/null
sleep 4
curl -fsS "$base/api/metrics" > "${RUNNER_TEMP:-/tmp}/netdm-radio-metrics.json"
curl -fsS "$base/api/history" > "${RUNNER_TEMP:-/tmp}/netdm-radio-history.json"
python3 - <<'PY'
import json, os
dir=os.environ.get("RUNNER_TEMP","/tmp")
with open(os.path.join(dir,"netdm-radio-metrics.json"),encoding="utf-8") as f: m=json.load(f)
with open(os.path.join(dir,"netdm-radio-history.json"),encoding="utf-8") as f: h=json.load(f)
b=m["benchmark"]
assert b["options"]["wirelessEnabled"] is True, b
assert m["nodeCount"] == 100, m
assert b["radio"]["configuredNodes"] <= 100, b
assert b["radio"]["totalWirelessLinksAdded"] > 0, b
assert b["radio"]["totalWirelessLinksRemoved"] > 0, b
assert b["radio"]["activeWirelessLinks"] >= 0, b
assert m["runtime"]["routes"] > 0, m
assert any(x["radioLinks"] > 0 for x in h), h
assert any(x["radioLinksRemovedPerSecond"] > 0 for x in h), h
print("Dynamic radio benchmark / topology churn / telemetry: PASS")
PY
curl -fsS -X POST "$base/api/benchmark/stop" >/dev/null

# Shared-medium model: a dense radio network remains sparse in the physical graph.
# The routing engine discovers neighbors lazily and reserves a per-channel airtime budget.
for i in $(seq 1 20); do
  status="$(curl -fsS "$base/api/metrics" | python3 -c 'import sys,json;print(json.load(sys.stdin)["benchmark"]["status"])')"
  if [[ "$status" == "stopped" || "$status" == "complete" ]]; then break; fi
  sleep 1
done
curl -fsS -X POST "$base/api/benchmark/start" \
  -H 'Content-Type: application/json' \
  -d '{"nodes":180,"routesPerSecond":60,"movesPerSecond":30,"linkChangesPerSecond":6,"durationSeconds":9,"seed":632,"wirelessEnabled":true,"radioModel":"shared","radioRange":128,"mediumBytesPerTick":512}' >/dev/null
sleep 4
curl -fsS "$base/api/metrics" > "${RUNNER_TEMP:-/tmp}/netdm-shared-metrics.json"
curl -fsS "$base/api/map?minX=-20&minY=-20&maxX=180&maxY=180&limit=1000" > "${RUNNER_TEMP:-/tmp}/netdm-shared-map.json"
curl -fsS "$base/api/history" > "${RUNNER_TEMP:-/tmp}/netdm-shared-history.json"
python3 - <<'PY'
import json, os
directory=os.environ.get("RUNNER_TEMP","/tmp")
def load(name):
    with open(os.path.join(directory,name),encoding="utf-8") as f: return json.load(f)
m=load("netdm-shared-metrics.json")
v=load("netdm-shared-map.json")
h=load("netdm-shared-history.json")
b=m["benchmark"]
assert b["options"]["radioModel"]=="shared" and b["options"]["wirelessEnabled"] is True, b
assert b["sharedMedium"]["configuredDevices"] <= 180, b
assert b["sharedMedium"]["domains"] == 1, b
assert b["sharedMedium"]["pairwiseRadioEdges"] == 0, b
assert m["nodeCount"] == 180 and m["edgeCount"] == 0, m
assert len(v["domainCells"]) > 0 and v["mediumVersion"] is not None, v
assert m["runtime"]["routes"] > 0, m
assert any(x["mediumDomains"] == 1 for x in h), h
assert b["sharedMedium"]["acceptedTransfers"]+b["sharedMedium"]["rejectedTransfers"]>0, b
radio=b["sharedMedium"]
reasons=[
  radio["rejectedStaleRoute"],radio["rejectedInvalidPath"],
  radio["rejectedTransferTooLarge"],radio["rejectedCapacity"],
  radio["rejectedOldTick"]
]
assert sum(reasons)==radio["rejectedTransfers"],radio
assert radio["attemptedWirelessHops"]>=radio["acceptedTransfers"],radio
assert radio["acceptedRevalidatedTransfers"]>=0,radio
assert all(sample["revalidatedTransfersPerSecond"]>=0 for sample in h),h
assert all(0<=sample["foundPercent"]<=100 for sample in h),h
assert all(0<=sample["mediumConfiguredDevices"]<=180 for sample in h),h
assert all(sample["schedulerQueued"]>=0 for sample in h),h
assert all(sample["rejectedTooLargePerSecond"]>=0 for sample in h),h
print("Shared-medium routing / domain cells / airtime diagnostics: PASS")
PY
curl -fsS -X POST "$base/api/benchmark/stop" >/dev/null

# The same scheduler is shared by IPC routes and background benchmark queries.
curl -fsS "$base/api/scheduler" > "${RUNNER_TEMP:-/tmp}/netdm-scheduler.json"
python3 - <<'PY'
import json, os
p=os.path.join(os.environ.get("RUNNER_TEMP","/tmp"),"netdm-scheduler.json")
with open(p,encoding="utf-8") as f: s=json.load(f)
assert 1 <= s["workers"] <= 8, s
assert 0 <= s["running"] <= s["workers"], s
assert 0 <= s["queued"] <= 256 and s["peakQueued"] <= 256, s
assert s["accepted"] > 0 and s["completed"] > 0, s
assert s["rejected"] >= 0 and s["queueWaitP95Us"] >= 0, s
assert s["budgetMillisecondsPer100Ms"] == 65, s
print("Global scheduled routing / bounded backlog / telemetry: PASS")
PY

# Admission accounting must be independent of scheduler completion.
curl -fsS "$base/api/metrics" > "${RUNNER_TEMP:-/tmp}/netdm-load-metrics.json"
python3 - <<'PY'
import json, os
path=os.path.join(os.environ.get("RUNNER_TEMP","/tmp"),"netdm-load-metrics.json")
with open(path,encoding="utf-8") as f: data=json.load(f)
load=data["benchmark"]["load"]
assert load["offered"] == load["admitted"] + load["rejected"],load
assert load["offered"] > 0,load
print("Offered, admitted and rejected arrival accounting: PASS",load)
PY

# One click in the dashboard starts the same server-owned suite. Exercise the
# entire three-stage lifecycle and verify each download independently.
grep -q 'RUN ALL THREE SCENARIOS' "${RUNNER_TEMP:-/tmp}/netdm-index.html"
curl -fsS -X POST "$base/api/benchmark-suite/start" \
  -H 'Content-Type: application/json' \
  -d '{"nodes":90,"routesPerSecond":45,"movesPerSecond":55,"linkChangesPerSecond":6,"durationSeconds":3,"seed":1337,"radioRange":14,"mediumBytesPerTick":8192,"sharedSearch":"astar-pooled","packetTransportEnabled":true,"spatialReuseEnabled":true,"packetMaxAgeTicks":100,"packetMaxAdvancesPerTick":60,"dedicatedPacketTicks":true,"packetTickWorkBudgetMs":25,"packetMaxNoRouteRetryDelayTicks":24,"packetRerouteQueueRetryDelayTicks":4}' \
  > "${RUNNER_TEMP:-/tmp}/netdm-suite-start.json"
python3 - <<'PY'
import json,os
p=os.path.join(os.environ.get("RUNNER_TEMP","/tmp"),"netdm-suite-start.json")
with open(p,encoding="utf-8") as f:s=json.load(f)
assert s["status"]=="running",s
assert s["runId"] and s["total"]==3,s
print("Three-scenario suite accepted:",s["runId"])
PY
# Manual starts must not overwrite a stage or its pending report snapshot.
http_code="$(curl -sS -o /dev/null -w '%{http_code}' -X POST "$base/api/benchmark/start" \
  -H 'Content-Type: application/json' \
  -d '{"nodes":90,"routesPerSecond":10,"durationSeconds":2}')"
test "$http_code" = "409"
for i in $(seq 1 65); do
  curl -fsS "$base/api/benchmark-suite" > "${RUNNER_TEMP:-/tmp}/netdm-suite-state.json"
  status="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["status"])' "${RUNNER_TEMP:-/tmp}/netdm-suite-state.json")"
  if [[ "$status" == "complete" ]]; then break; fi
  if [[ "$status" == "failed" || "$status" == "stopped" ]]; then
    cat "${RUNNER_TEMP:-/tmp}/netdm-suite-state.json" >&2
    exit 1
  fi
  sleep 1
done
run_id="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["runId"])' "${RUNNER_TEMP:-/tmp}/netdm-suite-state.json")"
for n in 1 2 3; do
  curl -fsS "$base/api/benchmark-suite/download/$n?runId=$run_id" \
    > "${RUNNER_TEMP:-/tmp}/netdm-suite-$n.json"
done
curl -fsS "$base/api/benchmark-suite/download-all?runId=$run_id" \
  > "${RUNNER_TEMP:-/tmp}/netdm-three-scenarios.zip"
python3 - <<'PY'
import json,os,zipfile
root=os.environ.get("RUNNER_TEMP","/tmp")
def load(filename):
    with open(os.path.join(root,filename),encoding="utf-8") as f:return json.load(f)
state=load("netdm-suite-state.json")
assert state["status"]=="complete" and state["completed"]==3,state
assert len(state["reports"])==3,state
scenarios=("static","movement","movement-outages")
for number,key in enumerate(scenarios,1):
    report=load(f"netdm-suite-{number}.json")
    assert report["suite"]["scenario"]==key,report["suite"]
    assert report["suite"]["number"]==number,report["suite"]
    assert report["suite"]["completed"] is True,report["suite"]
    assert report["suite"]["schemaVersion"]==1,report["suite"]
    delta=report["suite"]["schedulerDelta"]
    assert delta["accepted"]>=0 and delta["rejected"]>=0,delta
    assert delta["completed"]>=0 and delta["failed"]==0,delta
    assert report["suite"]["parameters"]["seed"]==1337,report["suite"]
    bench=report["metrics"]["benchmark"]
    assert bench["status"]=="complete",bench
    assert report["metrics"]["nodeCount"]==90,report["metrics"]
    assert len(report["history"])>0,report
    assert bench["load"]["offered"]==bench["load"]["admitted"]+bench["load"]["rejected"],bench
    assert bench["options"]["movesPerSecond"]==(0 if number==1 else 55),bench
    assert bench["options"]["linkChangesPerSecond"]==(6 if number==3 else 0),bench
    assert bench["options"]["sharedSearch"]=="astar-pooled",bench
    assert bench["options"]["packetTransportEnabled"] is True,bench
    assert bench["options"]["spatialReuseEnabled"] is True,bench
    assert bench["options"]["packetMaxAgeTicks"]==100,bench
    assert bench["options"]["packetMaxAdvancesPerTick"]==60,bench
    assert bench["options"]["dedicatedPacketTicks"] is True,bench
    assert bench["options"]["packetTickWorkBudgetMs"]==25,bench
    assert bench["options"]["packetMaxNoRouteRetryDelayTicks"]==24,bench
    assert bench["options"]["packetRerouteQueueRetryDelayTicks"]==4,bench
    assert bench["packetTransport"]["noRouteBackoffs"]>=0,bench
    assert bench["packetTransport"]["cooldownSkips"]>=0,bench
    assert bench["packetTransport"]["trackedChangedNodes"]>=0,bench
    assert bench["tickRunner"] is not None,bench
    assert bench["tickRunner"]["executedTicks"]>0,bench
    assert bench["tickRunner"]["failedTicks"]==0,bench
    assert bench["tickRunner"]["skippedIntervals"]>=0,bench
    assert bench["tickRunner"]["executionP95Milliseconds"]>=0,bench
    assert bench["packetTransport"] is not None,bench
    assert bench["rerouteScheduler"] is not None,bench
    assert bench["rerouteScheduler"]["workers"]==1,bench
    packet=bench["packetTransport"]
    assert packet["rerouteInFlight"]<=1,packet
    assert packet["rerouteQueued"]+packet["rerouteInFlight"]<=64,packet
    assert packet["rerouteCompleted"]<=packet["reroutes"],packet
    assert packet["rerouteSchedulerRejections"]<=packet["rerouteCancelled"],packet
    assert packet["firstHopAgeP95Ticks"]>=0,packet
    assert packet["firstHopSamples"]<=packet["wirelessHops"]+packet["cableHops"],packet
    assert bench["packetTransport"]["delivered"]+bench["packetTransport"]["dropped"]+bench["packetTransport"]["expired"]+bench["packetTransport"]["queued"]==bench["packetTransport"]["enqueued"],bench
with zipfile.ZipFile(os.path.join(root,"netdm-three-scenarios.zip")) as z:
    names=z.namelist()
    assert len(names)==3 and len(set(names))==3,names
    for number,key in enumerate(scenarios,1):
        match=[name for name in names if f"{number:02d}-{key}-" in name]
        assert len(match)==1,names
        content=json.loads(z.read(match[0]))
        assert content["suite"]["scenario"]==key,content
print("Three-stage benchmark suite + independent JSON files + ZIP: PASS")
PY

# New radio routing / transport modes. A* must be a selectable production
# candidate, while multi-tick packets are queued and forwarded separately
# from the legacy whole-path airtime reservation.
grep -q 'A\* · Admissible spatial heuristic' "${RUNNER_TEMP:-/tmp}/netdm-index.html"
grep -q 'HOPWISE · One hop per simulation tick' "${RUNNER_TEMP:-/tmp}/netdm-index.html"
curl -fsS -X POST "$base/api/benchmark/start" \
  -H 'Content-Type: application/json' \
  -d '{"nodes":121,"routesPerSecond":75,"movesPerSecond":0,"linkChangesPerSecond":0,"durationSeconds":7,"seed":1337,"wirelessEnabled":true,"radioModel":"shared","radioRange":14,"mediumBytesPerTick":512,"sharedSearch":"astar","packetTransportEnabled":true,"dedicatedPacketTicks":true,"packetTickWorkBudgetMs":25}' \
  > "${RUNNER_TEMP:-/tmp}/netdm-packet-start.json"
sleep 5
curl -fsS "$base/api/metrics" > "${RUNNER_TEMP:-/tmp}/netdm-packet-metrics.json"
curl -fsS "$base/api/history" > "${RUNNER_TEMP:-/tmp}/netdm-packet-history.json"
curl -fsS "$base/api/packets" > "${RUNNER_TEMP:-/tmp}/netdm-packet-stats.json"
python3 - <<'PY'
import json,os
root=os.environ.get("RUNNER_TEMP","/tmp")
def load(s):
    with open(os.path.join(root,s),encoding="utf-8") as f: return json.load(f)
m=load("netdm-packet-metrics.json")
h=load("netdm-packet-history.json")
stats=load("netdm-packet-stats.json")
bench=m["benchmark"]
assert bench["options"]["sharedSearch"]=="astar",bench
assert bench["options"]["packetTransportEnabled"] is True,bench
assert stats["enqueued"]>0,stats
assert stats["enqueued"]==stats["delivered"]+stats["dropped"]+stats["expired"]+stats["queued"],stats
assert stats["wirelessHops"]>0,stats
assert bench["options"]["spatialReuseEnabled"] is True,bench
assert bench["options"]["packetMaxAgeTicks"] == 900,bench
assert bench["sharedMedium"]["spatialSuccessfulHops"]>0,bench
assert bench["sharedMedium"]["spatialCellReservations"]>0,bench
assert stats["queueFullRejections"]+stats["invalidRejections"]==stats["rejected"],stats
assert stats["linkFailureDrops"]+stats["oversizeDrops"]<=stats["dropped"],stats
assert stats["deliveryAgeP95Ticks"]>=0,stats
assert stats["meanDeliveredHops"]>=0,stats
assert stats["rerouteQueued"]+stats["rerouteInFlight"]<=64,stats
assert stats["rerouteCompleted"]<=stats["reroutes"],stats
assert stats["firstHopAgeP95Ticks"]>=0,stats
assert stats["forwardingP95Milliseconds"]>=0,stats
assert stats["tickBudgetStops"]>=0,stats
assert stats["noRouteBackoffs"]>=0,stats
assert stats["noRouteBackoffTicks"]>=0,stats
assert stats["cooldownSkips"]>=0,stats
assert stats["localChangeWakeups"]>=0,stats
assert stats["recoveredOriginalHops"]>=0,stats
assert stats["staleNegativeResults"]>=0,stats
assert stats["trackedChangedNodes"]>=0,stats
assert bench["tickRunner"] is not None,bench
assert bench["tickRunner"]["executedTicks"]>0,bench
assert bench["tickRunner"]["failedTicks"]==0,bench
assert bench["tickRunner"]["startLagP95Milliseconds"]>=0,bench
assert bench["tickRunner"]["executionP95Milliseconds"]>=0,bench
assert bench["options"]["dedicatedPacketTicks"] is True,bench
assert bench["options"]["packetTickWorkBudgetMs"]==25,bench
assert m["benchmark"]["rerouteScheduler"]["workers"]==1,m
assert stats["peakQueued"]<=2048,stats
assert stats["lastTick"]>=0,stats
assert all(x["packetsQueued"]>=0 for x in h),h
assert all(x["deliveryP95AgeTicks"]>=0 for x in h),h
assert all(x["spatialBusyHopsPerSecond"]>=0 for x in h),h
assert all(x["spatialSuccessfulHopsPerSecond"]>=0 for x in h),h
assert all(x["noRouteDeferralsPerSecond"]>=0 for x in h),h
assert all(x["rerouteStartedPerSecond"]>=0 for x in h),h
assert all(x["rerouteCompletedPerSecond"]>=0 for x in h),h
assert all(x["packetSkippedTicksPerSecond"]>=0 for x in h),h
assert all(x["packetTickAdmissionFailuresPerSecond"]>=0 for x in h),h
assert all(x["noRouteBackoffsPerSecond"]>=0 for x in h),h
assert all(x["rerouteCooldownSkipsPerSecond"]>=0 for x in h),h
assert all(x["localChangeWakeupsPerSecond"]>=0 for x in h),h
assert all(x["recoveredOriginalHopsPerSecond"]>=0 for x in h),h
assert all(x["staleNegativeResultsPerSecond"]>=0 for x in h),h
assert all(x["averageNoRouteBackoffTicks"]>=0 for x in h),h
assert all(x["tickExecutedPerSecond"]>=0 for x in h),h
assert all(x["tickRunnerSkippedPerSecond"]>=0 for x in h),h
assert all(x["tickBudgetStopsPerSecond"]>=0 for x in h),h
assert all(x["tickStartLagP95Milliseconds"]>=0 for x in h),h
assert all(x["tickExecutionP95Milliseconds"]>=0 for x in h),h
assert all(x["tickRunnerFailed"]==0 for x in h),h
assert all(x["packetFirstHopP95Ticks"]>=0 for x in h),h
assert all(x["rerouteCpuWaitP95Us"]>=0 for x in h),h
assert all(x["packetsDeliveredPerSecond"]>=0 for x in h),h
assert all(x["exploredPerRoute"]>=0 for x in h),h
assert all(x["allocatedBytesPerRoute"]>=0 for x in h),h
assert all(x["gcPauseMillisecondsPerSecond"]>=0 for x in h),h
assert m["runtime"]["totalExploredNodes"]>=0,m
assert m["runtime"]["totalRouteAllocatedBytes"]>=0,m
assert m["runtime"]["gcTotalPauseMilliseconds"]>=0,m
print("A* radio + packet forwarding + GC diagnostics HTTP integration: PASS")
PY
curl -fsS -X POST "$base/api/benchmark/stop" >/dev/null

# Dedicated tick mode is the default for new hopwise experiments. The old
# shared-queue mode remains available for A/B comparison; do not conflate
# "offered routes" with tick cadence.
grep -q 'DEDICATED · Monotonic 100ms runner' "${RUNNER_TEMP:-/tmp}/netdm-index.html"
grep -q 'Shared global CPU queue (baseline)' "${RUNNER_TEMP:-/tmp}/netdm-index.html"

# A/B controls must be visibly available in the same dashboard.
grep -q 'A\* POOLED' "${RUNNER_TEMP:-/tmp}/netdm-index.html"
grep -q 'Controlled A/B experiment presets' "${RUNNER_TEMP:-/tmp}/netdm-index.html"
