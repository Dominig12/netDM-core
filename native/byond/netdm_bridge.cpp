// BYOND string call_ext ABI: extern "C" char* __cdecl name(uint32_t,char**).
// This 32-bit DLL does no routing or blocking host IO on the DreamDaemon thread.
// A single background worker connects to the local 64-bit NetDM.Host pipe.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <utility>

namespace {
constexpr const char* kPipe = R"(\\.\pipe\netdm-core)";
constexpr size_t kMaxLine = 60 * 1024;
constexpr size_t kOutbound = 256;
constexpr size_t kInbound = 1024;

std::mutex g_mutex;
std::deque<std::string> g_outbound;
std::deque<std::string> g_inbound;
std::thread g_worker;
std::atomic<bool> g_stop{false};
std::atomic<bool> g_online{false};
std::atomic<bool> g_started{false};
std::atomic<uint64_t> g_lost{0};

char* answer(std::string value) {
    // BYOND copies the returned char* before leaving call_ext.
    thread_local std::string buffer;
    buffer = std::move(value);
    return buffer.data();
}

void pushEvent(std::string event) {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_inbound.size() == kInbound) {
        g_inbound.pop_front();
        g_lost.fetch_add(1, std::memory_order_relaxed);
    }
    g_inbound.push_back(std::move(event));
}

void disconnected() {
    g_online.store(false, std::memory_order_release);
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        // Nothing is resent automatically: the server could have executed
        // the command before disconnecting. Let DM resync, never duplicate.
        g_outbound.clear();
    }
    pushEvent(R"({"bridge":"disconnected","ok":false,"error":"connection_lost_resynchronize"})");
}

void worker() {
    while (!g_stop.load(std::memory_order_acquire)) {
        HANDLE pipe = CreateFileA(kPipe, GENERIC_READ | GENERIC_WRITE, 0,
            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) {
            Sleep(100);
            continue;
        }
        g_online.store(true, std::memory_order_release);
        pushEvent(R"({"bridge":"connected","ok":true})");
        std::string input;
        bool failed = false;
        while (!g_stop.load(std::memory_order_acquire) && !failed) {
            std::string outgoing;
            {
                std::lock_guard<std::mutex> lock(g_mutex);
                if (!g_outbound.empty()) {
                    outgoing = std::move(g_outbound.front());
                    g_outbound.pop_front();
                }
            }
            if (!outgoing.empty()) {
                outgoing.push_back('\n');
                DWORD count = 0;
                if (!WriteFile(pipe, outgoing.data(),
                        static_cast<DWORD>(outgoing.size()), &count, nullptr) ||
                    count != outgoing.size()) {
                    failed = true;
                    break;
                }
            }
            DWORD available = 0;
            if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr)) {
                failed = true;
                break;
            }
            if (available > 0) {
                char chunk[4096];
                const DWORD toRead = available < sizeof(chunk)
                    ? available : static_cast<DWORD>(sizeof(chunk));
                DWORD read = 0;
                if (!ReadFile(pipe, chunk, toRead, &read, nullptr) || read == 0) {
                    failed = true;
                    break;
                }
                input.append(chunk, read);
                size_t end = 0;
                while ((end = input.find('\n')) != std::string::npos) {
                    std::string line = input.substr(0, end);
                    input.erase(0, end + 1);
                    if (!line.empty() && line.back() == '\r') line.pop_back();
                    if (line.size() > kMaxLine) {
                        pushEvent(R"({"bridge":"error","ok":false,"error":"oversized_host_frame"})");
                        failed = true;
                        break;
                    }
                    pushEvent(std::move(line));
                }
                if (input.size() > kMaxLine) {
                    pushEvent(R"({"bridge":"error","ok":false,"error":"oversized_host_frame"})");
                    failed = true;
                }
            } else {
                Sleep(5);
            }
        }
        CloseHandle(pipe);
        if (failed && !g_stop.load(std::memory_order_acquire))
            disconnected();
        else g_online.store(false, std::memory_order_release);
    }
    g_started.store(false, std::memory_order_release);
}

bool start() {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_started.load(std::memory_order_acquire)) return true;
    if (g_worker.joinable()) return false;
    g_stop.store(false, std::memory_order_release);
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

extern "C" __declspec(dllexport) char* __cdecl netdm_init(
    unsigned int, char*[]) {
    return answer(start() ? "1" : "0");
}

extern "C" __declspec(dllexport) char* __cdecl netdm_send(
    unsigned int count, char* args[]) {
    if (count != 1 || !args || !args[0]) return answer("0");
    const std::string value(args[0]);
    if (value.empty() || value.size() > kMaxLine ||
        value.find('\n') != std::string::npos ||
        value.find('\r') != std::string::npos) return answer("0");
    if (!g_started.load(std::memory_order_acquire) && !start()) return answer("0");
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_outbound.size() >= kOutbound) return answer("0");
    g_outbound.push_back(value);
    return answer("1");
}

extern "C" __declspec(dllexport) char* __cdecl netdm_poll(
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

extern "C" __declspec(dllexport) char* __cdecl netdm_status(
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
        R"(,"lostResponses":)" + std::to_string(lost) + "}");
}

extern "C" __declspec(dllexport) char* __cdecl netdm_shutdown(
    unsigned int, char*[]) {
    g_stop.store(true, std::memory_order_release);
    if (g_worker.joinable()) {
        // Interrupt a worker stuck in a synchronous pipe operation during
        // clean shutdown, not on an ordinary DM network tick.
        CancelSynchronousIo(static_cast<HANDLE>(g_worker.native_handle()));
        g_worker.join();
    }
    g_online.store(false, std::memory_order_release);
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        g_outbound.clear();
        g_inbound.clear();
    }
    return answer("1");
}
