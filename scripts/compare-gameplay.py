#!/usr/bin/env python3
"""Short J/K/L/M/N suites through Host API; correctness, not a performance gate."""
import argparse
import json
import os
from pathlib import Path
import sys
import time
import urllib.parse
import urllib.request
import zipfile

CASES = ("static", "movement", "movement-outages")


def api(base, endpoint, payload=None):
    body = None if payload is None else json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(base + endpoint, data=body,
                                 headers={"Content-Type": "application/json"} if body else {})
    with urllib.request.urlopen(req, timeout=30) as result:
        return json.load(result)


def collect(base, options, mode, out):
    config = dict(options, prioritizeInteractiveTraffic=mode in ("K", "L", "M", "N"),
                  interactiveReservedSlots=32 if mode in ("K", "L", "M", "N") else 0,
                  prioritizeInteractiveReroutes=mode in ("L", "M", "N"),
                  interactiveRerouteReservedSlots=4 if mode in ("L", "M", "N") else 0,
                  prioritizeInteractiveAirtime=mode in ("M", "N"),
                  interactiveAirtimeReservePercent=25,
                  backgroundCongestionHighPercent=40 if mode == "N" else 0,
                  backgroundCongestionLowPercent=20 if mode == "N" else 60,
                  interactiveLatencyObjectiveTicks=10)
    accepted = api(base, "/api/benchmark-suite/start", config)
    assert accepted["status"] in ("running", "complete"), accepted
    run_id = accepted["runId"]
    assert run_id
    deadline = time.monotonic() + 240
    while time.monotonic() < deadline:
        state = api(base, "/api/benchmark-suite")
        if state["status"] == "complete":
            break
        if state["status"] in ("failed", "stopped"):
            raise RuntimeError(f"{mode} {state['status']}: {state.get('error')}")
        time.sleep(0.5)
    else:
        raise TimeoutError(f"{mode} suite timed out")
    assert state["runId"] == run_id and state["completed"] == 3, state
    url = base + "/api/benchmark-suite/download-all?runId=" + urllib.parse.quote(run_id)
    with urllib.request.urlopen(url, timeout=60) as response:
        (out / f"{mode}.zip").write_bytes(response.read())
    rows = []
    with zipfile.ZipFile(out / f"{mode}.zip") as archive:
        names = sorted(n for n in archive.namelist() if n.endswith(".json"))
        assert len(names) == 3, names
        for name, expected in zip(names, CASES):
            report = json.loads(archive.read(name))
            suite = report["suite"]
            assert suite["scenario"] == expected, suite
            assert suite["parameters"]["seed"] == options["seed"], suite
            assert suite["parameters"]["trafficProfile"] == "ss13-mixed", suite
            assert suite["parameters"]["prioritizeInteractiveTraffic"] == (mode in ("K", "L", "M", "N")), suite
            assert suite["parameters"]["prioritizeInteractiveReroutes"] == (mode in ("L", "M", "N")), suite
            assert suite["parameters"]["interactiveRerouteReservedSlots"] == (4 if mode in ("L", "M", "N") else 0), suite
            assert suite["parameters"]["prioritizeInteractiveAirtime"] == (mode in ("M", "N")), suite
            assert suite["parameters"]["backgroundCongestionHighPercent"] == (40 if mode == "N" else 0), suite
            assert suite["parameters"]["backgroundCongestionLowPercent"] == (20 if mode == "N" else 60), suite
            assert suite["parameters"]["interactiveLatencyObjectiveTicks"] == 10, suite
            assert suite["parameters"]["interactiveAirtimeReservePercent"] == 25, suite
            b = report["metrics"]["benchmark"]
            assert b["status"] == "complete" and b["tickRunner"]["failedTicks"] == 0, b
            load, p = b["load"], b["packetTransport"]
            radio = b["sharedMedium"]
            assert radio is not None, b
            assert load["offered"] == load["interactiveOffered"] + load["backgroundOffered"]
            assert load["offered"] == load["admitted"] + load["rejected"]
            assert load["admitted"] == load["interactiveAdmitted"] + load["backgroundAdmitted"]
            assert load["rejected"] == load["interactiveRejected"] + load["backgroundRejected"]
            assert load["interactiveOffered"] > 0 and load["backgroundOffered"] > 0
            assert p["enqueued"] == p["delivered"] + p["dropped"] + p["expired"] + p["queued"]
            assert p["delivered"] == p["interactiveDelivered"] + p["backgroundDelivered"]
            assert p["queued"] == p["interactiveQueued"] + p["backgroundQueued"]
            assert p["queued"] <= config["packetQueueCapacity"]
            assert p["reservedAdmissions"] == 0
            assert p["rerouteQueued"] == (
                p["interactiveRerouteQueued"] + p["backgroundRerouteQueued"])
            assert p["rerouteInFlight"] == (
                p["interactiveRerouteInFlight"] + p["backgroundRerouteInFlight"])
            assert p["capacityBlockedPackets"] == (
                p["interactiveCapacityBlocked"] + p["backgroundCapacityBlocked"])
            assert p["reroutes"] == (
                p["interactiveReroutesStarted"] + p["backgroundReroutesStarted"])
            assert p["interactiveRerouteWaitP95Ticks"] >= 0
            assert p["backgroundRerouteWaitP95Ticks"] >= 0
            assert p["interactiveDelivered"] > 0 and p["backgroundDelivered"] > 0
            assert p["expired"] == p["interactiveExpired"] + p["backgroundExpired"]
            assert p["dropped"] == p["interactiveDropped"] + p["backgroundDropped"]
            assert p["interactiveFirstHopSamples"] + p["backgroundFirstHopSamples"] == p["firstHopSamples"]
            assert p["interactiveDeliveryAgeP99Ticks"] >= p["interactiveDeliveryAgeP95Ticks"]
            assert p["backgroundDeliveryAgeP99Ticks"] >= p["backgroundDeliveryAgeP95Ticks"]
            assert p["interactiveFirstHopAgeP99Ticks"] >= p["interactiveFirstHopAgeP95Ticks"]
            assert p["backgroundFirstHopAgeP99Ticks"] >= p["backgroundFirstHopAgeP95Ticks"]
            assert 0 <= p["interactiveOutstandingBeyondObjective"] <= p["interactiveQueued"]
            assert p["interactiveCompletedBeyondObjective"] <= p["interactiveDelivered"]
            assert p["backgroundCongestionRejections"] >= 0
            assert radio["interactiveHopTransmissions"] >= 0
            assert radio["backgroundHopTransmissions"] >= 0
            assert radio["interactiveHopTransmissions"] + radio["backgroundHopTransmissions"] == p["wirelessHops"]
            assert radio["protectedBackgroundDeferrals"] >= 0
            assert radio["interactiveHopBytes"] >= radio["interactiveHopTransmissions"]
            assert radio["backgroundHopBytes"] >= radio["backgroundHopTransmissions"]
            assert report["history"]
            assert all("protectedBackgroundAirtimeDeferralsPerSecond" in row
                       and "interactiveFirstHopP99Ticks" in row
                       and "interactiveOutstandingBeyondObjective" in row
                       and "backgroundCongestionRejectionsPerSecond" in row
                       for row in report["history"])
            rows.append(dict(mode=mode, scenario=expected, offered=load["offered"],
                             delivered=p["delivered"], interactiveDelivered=p["interactiveDelivered"],
                             backgroundDelivered=p["backgroundDelivered"],
                             interactiveP95Ticks=p["interactiveDeliveryAgeP95Ticks"],
                             backgroundP95Ticks=p["backgroundDeliveryAgeP95Ticks"],
                             firstHopP95Ticks=p["firstHopAgeP95Ticks"],
                             peakQueued=p["peakQueued"],
                             dropped=p["dropped"], expired=p["expired"],
                             skippedTicks=b["tickRunner"]["skippedIntervals"],
                             interactiveReroutes=p["interactiveReroutesStarted"],
                             backgroundReroutes=p["backgroundReroutesStarted"],
                             interactiveRerouteWaitP95=p["interactiveRerouteWaitP95Ticks"],
                             backgroundRerouteWaitP95=p["backgroundRerouteWaitP95Ticks"],
                             protectedAirtimeDeferrals=radio["protectedBackgroundDeferrals"],
                             interactiveRadioBytes=radio["interactiveHopBytes"],
                             backgroundRadioBytes=radio["backgroundHopBytes"],
                             interactiveFirstHopP99=p["interactiveFirstHopAgeP99Ticks"],
                             interactiveDeliveryP99=p["interactiveDeliveryAgeP99Ticks"],
                             interactiveOutstandingBeyond=p["interactiveOutstandingBeyondObjective"],
                             interactiveDropped=p["interactiveDropped"],
                             interactiveExpired=p["interactiveExpired"],
                             congestionRejected=p["backgroundCongestionRejections"]))
    return rows


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:5087")
    parser.add_argument("--out", default="benchmarks/results/gameplay-ci")
    parser.add_argument("--seed", type=int, default=1337)
    args = parser.parse_args()
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    config = dict(nodes=512, routesPerSecond=70, movesPerSecond=40,
                  linkChangesPerSecond=2, durationSeconds=6, seed=args.seed,
                  radioRange=14, mediumBytesPerTick=8192,
                  sharedSearch="astar-dense", packetTransportEnabled=True,
                  spatialReuseEnabled=True, packetMaxAgeTicks=900,
                  packetMaxAdvancesPerTick=1024, packetRerouteWorkers=2,
                  packetRerouteQueueCapacity=32, packetRerouteCpuBudgetMs=25,
                  dedicatedPacketTicks=True, packetTickWorkBudgetMs=25,
                  packetMaxNoRouteRetryDelayTicks=96,
                  packetRerouteQueueRetryDelayTicks=8,
                  packetQueueCapacity=512, adaptivePacketVisits=True,
                  packetPreRouteBackpressure=True,
                  eventDrivenPacketWaiting=True, eventDrivenRerouteCapacity=True,
                  trafficProfile="ss13-mixed", interactiveTrafficPercent=20,
                  localTrafficPercent=85, burstPeriodSeconds=4,
                  burstDurationSeconds=1, burstMultiplier=3)
    for attempt in range(100):
        try:
            if api(args.url, "/api/health")["status"] == "ok":
                break
        except (OSError, ValueError, KeyError, TimeoutError):
            time.sleep(0.3)
    else:
        raise RuntimeError("NetDM Host not ready")

    rows = []
    for mode in ("J", "K", "L", "M", "N"):
        rows.extend(collect(args.url, config, mode, out))
        print(f"{mode}: PASS", flush=True)

    summary = [
        "## SS13 J/K/L/M/N automated smoke",
        "",
        "**6s/scenario on a shared CI runner: correctness only, NOT a latency/throughput gate.**",
        "",
        "| Scenario | Mode | Offered | Delivered | Interactive | Background | "
        "Interactive P95 (ticks) | Background P95 (ticks) | "
        "Interactive reroutes | Protected airtime | Congestion rejections | "
        "Interactive P99 (ticks) | Pending beyond objective | Runner skips |",
        "|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|",
    ]
    for row in rows:
        summary.append(
            f"| {row['scenario']} | {row['mode']} | {row['offered']} | "
            f"{row['delivered']} | {row['interactiveDelivered']} | "
            f"{row['backgroundDelivered']} | {row['interactiveP95Ticks']} | "
            f"{row['backgroundP95Ticks']} | {row['interactiveReroutes']} | "
            f"{row['protectedAirtimeDeferrals']} | {row['congestionRejected']} | "
            f"{row['interactiveDeliveryP99']} | {row['interactiveOutstandingBeyond']} | "
            f"{row['skippedTicks']} |"
        )
    text = "\n".join(summary) + "\n"
    (out / "summary.md").write_text(text, encoding="utf-8")
    (out / "summary.json").write_text(
        json.dumps(dict(config=config, environment="GitHub Actions shared runner",
                        purpose="correctness smoke, no perf threshold", results=rows), indent=2),
        encoding="utf-8")
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as f:
            f.write(text)
    print(text)
    print("J/K/L/M/N comparison passed", flush=True)


if __name__ == "__main__":
    main()
