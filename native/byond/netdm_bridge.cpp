// BYOND string call_ext ABI: extern "C" char* __cdecl name(uint32_t,char**).
// A single worker owns all IPC to the local x64 NetDM.Host.
#include "local_stream.h"
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>
#include <utility>

namespace {
constexpr size_t kMaxLegacyBody = 60 * 1024;
constexpr size_t kMaxFrameBody = 48 * 1024;
constexpr size_t kOutbound = 256;
constexpr size_t kInbound = 1024;
// Bounded work per worker iteration, so one direction cannot starve the other.
constexpr int kWritesPerIteration = 32;
constexpr int kReadsPerIteration = 64;

std::mutex g_mutex;
std::deque<std::string> g_outbound;
std::deque<std::string> g_inbound;
std::thread g_worker;
std::atomic<bool> g_stop{false};
std::atomic<bool> g_online{false};
std::atomic<bool> g_started{false};
std::atomic<bool> g_reconnect{false};
std::atomic<uint64_t> g_lost{0};
// Worker diagnostics: a stalled worker stops counting iterations; a silent peer stops bytes.
std::atomic<uint64_t> g_iterations{0};
std::atomic<uint64_t> g_frames_out{0};
std::atomic<uint64_t> g_bytes_in{0};
std::atomic<uint64_t> g_frames_in{0};
std::atomic<int> g_phase{0}; // 0 sleeping, 1 writing, 2 reading, 3 connecting.
std::atomic<int64_t> g_last_iteration_ms{0};
std::atomic<int64_t> g_max_gap_ms{0};
#ifdef _WIN32
// netdm_send signals it, so a queued frame never waits for the idle timeout.
HANDLE g_wake = nullptr;
bool g_std_sleep = false; // NETDM_BRIDGE_WAIT=std keeps std::this_thread::sleep_for for comparison.
#endif

int64_t nowMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

/// Idle wait of the worker. On Windows a Win32 event wait replaces sleep_for: the MinGW
/// sleep_for was observed to stall the worker for seconds under DreamDaemon.
void idle(std::chrono::milliseconds duration) {
#ifdef _WIN32
    if (!g_std_sleep && g_wake) {
        WaitForSingleObject(g_wake, static_cast<DWORD>(duration.count()));
        return;
    }
#endif
    std::this_thread::sleep_for(duration);
}
bool g_framed = false; // Set under g_mutex before starting the worker.

char* answer(std::string_view value) {
    // BYOND copies the returned char* before leaving call_ext.
    // A trivial buffer avoids a TLS destructor in an unloadable library.
    thread_local char buffer[kMaxLegacyBody + 1];
    std::memcpy(buffer, value.data(), value.size());
    buffer[value.size()] = '\0';
    return buffer;
}

bool pushEvent(std::string event) {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_reconnect.load()) return false;
    if (g_inbound.size() == kInbound) {
        g_lost.fetch_add(1, std::memory_order_relaxed);
        if (g_framed) return false;
        g_inbound.pop_front();
    }
    g_inbound.push_back(std::move(event));
    return true;
}

void disconnected() {
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        g_online.store(false, std::memory_order_release);
        g_reconnect.store(false);
        // Nothing is resent automatically: the server could have executed
        // the command before disconnecting. Let DM resync, never duplicate.
        g_outbound.clear();
        if (g_framed) g_inbound.clear();
    }
    pushEvent(R"({"bridge":"disconnected","ok":false,"error":"connection_lost_resynchronize"})");
}

std::string encode(std::string body) {
    if (!g_framed) {
        body.push_back('\n');
        return body;
    }
    const auto length = body.size();
    const char header[] = {0, 0, static_cast<char>(length >> 8), static_cast<char>(length)};
    body.insert(0, header, sizeof(header));
    return body;
}

bool receive(std::string& input) {
    while (!input.empty()) {
        size_t offset = 0, length = 0, consumed = 0;
        if (g_framed) {
            if (input.size() < 4) return true;
            for (size_t i = 0; i < 4; ++i)
                length = (length << 8) | static_cast<unsigned char>(input[i]);
            if (length == 0 || length > kMaxFrameBody) return false;
            offset = 4;
            consumed = offset + length;
            if (input.size() < consumed) return true;
        } else {
            length = input.find('\n');
            if (length == std::string::npos) return input.size() <= kMaxLegacyBody + 1;
            consumed = length + 1;
            if (length > 0 && input[length - 1] == '\r') --length;
            if (length > kMaxLegacyBody) return false;
        }
        if (input.find('\0', offset) < offset + length ||
            !pushEvent(input.substr(offset, length))) return false;
        g_frames_in.fetch_add(1, std::memory_order_relaxed);
        input.erase(0, consumed);
    }
    return true;
}

void worker() {
    using namespace std::chrono_literals;
    using Clock = std::chrono::steady_clock;
    while (!g_stop.load(std::memory_order_acquire)) {
        LocalStream stream;
        g_phase.store(3, std::memory_order_relaxed);
        if (!stream.open()) {
            idle(100ms);
            continue;
        }
        g_online.store(true, std::memory_order_release);
        if (!pushEvent(R"({"bridge":"connected","ok":true})")) {
            disconnected();
            continue;
        }
        std::string input;
        auto partialSince = Clock::now();
        bool healthy = true;
        while (healthy && !g_stop.load(std::memory_order_acquire) && !g_reconnect.load()) {
            bool progressed = false;
            g_iterations.fetch_add(1, std::memory_order_relaxed);
            const auto now = nowMs();
            const auto last = g_last_iteration_ms.exchange(now, std::memory_order_relaxed);
            if (last && now - last > g_max_gap_ms.load(std::memory_order_relaxed))
                g_max_gap_ms.store(now - last, std::memory_order_relaxed);
            g_phase.store(1, std::memory_order_relaxed);
            for (int i = 0; healthy && i < kWritesPerIteration; ++i) {
                std::string outgoing;
                {
                    std::lock_guard<std::mutex> lock(g_mutex);
                    if (g_outbound.empty()) break;
                    outgoing = std::move(g_outbound.front());
                    g_outbound.pop_front();
                }
                healthy = stream.write(encode(std::move(outgoing)), g_stop);
                if (healthy) g_frames_out.fetch_add(1, std::memory_order_relaxed);
                progressed = true;
            }
            g_phase.store(2, std::memory_order_relaxed);
            for (int i = 0; healthy && i < kReadsPerIteration; ++i) {
                char chunk[4096];
                const auto count = stream.read(chunk, sizeof(chunk), g_stop);
                if (count < 0) healthy = false;
                if (count <= 0) break;
                g_bytes_in.fetch_add(static_cast<uint64_t>(count), std::memory_order_relaxed);
                const auto pending = input.size();
                input.append(chunk, count);
                const auto appended = input.size();
                healthy = receive(input);
                // The stall deadline measures time without completing a frame.
                if (pending == 0 || input.size() < appended) partialSince = Clock::now();
                progressed = true;
            }
            if (healthy && !input.empty() && Clock::now() - partialSince > 5s) healthy = false;
            g_phase.store(0, std::memory_order_relaxed);
            if (healthy && !progressed) idle(2ms);
        }
        if (!g_stop.load(std::memory_order_acquire))
            disconnected();
        else g_online.store(false, std::memory_order_release);
    }
    g_started.store(false, std::memory_order_release);
}

bool start() {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_started.load(std::memory_order_acquire)) return true;
    if (g_worker.joinable()) return false;
    const char* configured = std::getenv("NETDM_IPC_MODE");
    const std::string_view mode = configured ? configured : "legacy";
    if (mode != "v1" && mode != "legacy") return false;
    g_framed = mode == "v1";
#ifndef _WIN32
    if (!g_framed) return false;
#endif
    g_stop.store(false, std::memory_order_release);
    g_reconnect.store(false);
#ifdef _WIN32
    const char* wait = std::getenv("NETDM_BRIDGE_WAIT");
    g_std_sleep = wait && std::string_view(wait) == "std";
    if (!g_wake) g_wake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
#endif
    try {
        g_started.store(true, std::memory_order_release);
        g_worker = std::thread(worker);
        return true;
    } catch (...) {
        g_started.store(false, std::memory_order_release);
        return false;
    }
}
} // namespace

#ifdef _WIN32
#define NETDM_EXPORT extern "C" __declspec(dllexport) char* __cdecl
#else
#define NETDM_EXPORT extern "C" __attribute__((visibility("default"))) char*
#endif

NETDM_EXPORT netdm_init(
    unsigned int, char*[]) {
    return answer(start() ? "1" : "0");
}

NETDM_EXPORT netdm_send(
    unsigned int count, char* args[]) {
    if (count != 1 || !args || !args[0]) return answer("0");
    if (!g_started.load(std::memory_order_acquire) && !start()) return answer("0");
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_outbound.size() >= kOutbound || (g_framed && !g_online.load())) return answer("0");
    const size_t max = g_framed ? kMaxFrameBody : kMaxLegacyBody;
    size_t length = 0;
    while (length <= max && args[0][length]) ++length;
    if (length == 0 || length > max) return answer("0");
    const std::string_view value(args[0], length);
    if (!g_framed && value.find_first_of("\r\n") != std::string_view::npos) return answer("0");
    g_outbound.emplace_back(value);
#ifdef _WIN32
    if (g_wake) SetEvent(g_wake);
#endif
    return answer("1");
}

NETDM_EXPORT netdm_poll(
    unsigned int, char*[]) {
    std::string result;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        if (g_inbound.empty()) return answer("");
        result = std::move(g_inbound.front());
        g_inbound.pop_front();
    }
    return answer(std::move(result));
}

NETDM_EXPORT netdm_status(
    unsigned int, char*[]) {
    const bool online = g_online.load(std::memory_order_acquire);
    const uint64_t lost = g_lost.load(std::memory_order_relaxed);
    size_t outgoing, incoming;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        outgoing = g_outbound.size();
        incoming = g_inbound.size();
    }
    return answer(std::string(R"({"started":)") +
        (g_started.load(std::memory_order_acquire) ? "true" : "false") +
        R"(,"connected":)" + (online ? "true" : "false") +
        R"(,"outboundQueued":)" + std::to_string(outgoing) +
        R"(,"inboundQueued":)" + std::to_string(incoming) +
        R"(,"lostResponses":)" + std::to_string(lost) +
        R"(,"iterations":)" + std::to_string(g_iterations.load(std::memory_order_relaxed)) +
        R"(,"framesOut":)" + std::to_string(g_frames_out.load(std::memory_order_relaxed)) +
        R"(,"framesIn":)" + std::to_string(g_frames_in.load(std::memory_order_relaxed)) +
        R"(,"bytesIn":)" + std::to_string(g_bytes_in.load(std::memory_order_relaxed)) +
        R"(,"phase":)" + std::to_string(g_phase.load(std::memory_order_relaxed)) +
        R"(,"sinceIterationMs":)" + std::to_string(nowMs() - g_last_iteration_ms.load(std::memory_order_relaxed)) +
        R"(,"maxGapMs":)" + std::to_string(g_max_gap_ms.load(std::memory_order_relaxed)) + "}");
}

NETDM_EXPORT netdm_reconnect(unsigned int, char*[]) {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_framed || !g_online.load()) return answer("0");
    g_online.store(false, std::memory_order_release);
    g_reconnect.store(true);
    g_inbound.clear();
    return answer("1");
}

NETDM_EXPORT netdm_shutdown(
    unsigned int, char*[]) {
    g_stop.store(true, std::memory_order_release);
    if (g_worker.joinable()) g_worker.join();
    g_online.store(false, std::memory_order_release);
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        g_outbound.clear();
        g_inbound.clear();
    }
    return answer("1");
}
