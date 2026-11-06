// Native Win32 x86 integration test. Loads exactly the BYOND exports and
// exercises the real localhost named pipe to a separate x64 .NET Host.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <chrono>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

using ByondFn = char* (__cdecl *)(unsigned int, char**);

struct Api {
    HMODULE dll{};
    ByondFn init{}, send{}, poll{}, status{}, shutdown{};
    Api() {
        dll = LoadLibraryA("netdm_byond.dll");
        if (!dll) throw std::runtime_error("LoadLibraryA netdm_byond.dll failed");
        auto resolve = [this](const char* name) -> ByondFn {
            auto f = reinterpret_cast<ByondFn>(GetProcAddress(dll, name));
            if (!f) throw std::runtime_error(std::string("Missing export ") + name);
            return f;
        };
        init=resolve("netdm_init"); send=resolve("netdm_send");
        poll=resolve("netdm_poll"); status=resolve("netdm_status");
        shutdown=resolve("netdm_shutdown");
    }
    ~Api() {
        if (shutdown) shutdown(0,nullptr);
        if (dll) FreeLibrary(dll);
    }
    std::string invoke(ByondFn fn, std::string body = {}) {
        char* data = body.empty() ? nullptr : body.data();
        char* args[] = {data};
        char* result = fn(data ? 1u : 0u, data ? args : nullptr);
        return result ? result : "";
    }
};

void check(bool cond, const char* message) {
    if (!cond) throw std::runtime_error(message);
}

std::string waitReply(Api& api, const std::string& id, int seconds = 10) {
    const auto until = std::chrono::steady_clock::now() +
        std::chrono::seconds(seconds);
    const std::string key = "\"id\":\"" + id + "\"";
    while (std::chrono::steady_clock::now() < until) {
        auto result = api.invoke(api.poll);
        if (result.find("\"bridge\":\"disconnected\"") != std::string::npos)
            throw std::runtime_error("Unexpected host pipe disconnect");
        if (result.find(key) != std::string::npos)
            return result;
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    throw std::runtime_error("Timeout waiting for " + id);
}

int main() {
    try {
        Api api;
        check(api.invoke(api.init) == "1", "Failed to launch shim worker");
        auto limit = std::chrono::steady_clock::now() + std::chrono::seconds(15);
        while (std::chrono::steady_clock::now() < limit &&
               api.invoke(api.status).find("\"connected\":true") == std::string::npos)
            std::this_thread::sleep_for(std::chrono::milliseconds(50));
        check(api.invoke(api.status).find("\"connected\":true") != std::string::npos,
              "Native x86 shim failed to connect to x64 Host");

        // Reuse only the single string argument required by classic BYOND ABI.
        const std::string setup =
            R"({"id":"setup","op":"game_batch","changes":[)"
            R"({"op":"add_node","a":1,"x":0,"y":0},)"
            R"({"op":"add_node","a":2,"x":10,"y":0}]})";
        check(api.invoke(api.send,setup) == "1","Cannot enqueue topology");
        auto a=waitReply(api,"setup");
        check(a.find("\"ok\":true")!=std::string::npos &&
              a.find("\"applied\":2")!=std::string::npos,
              "Game topology mutation failed");

        const std::string radio1 =
            R"({"id":"radio1","op":"game_radio","a":1,"enabled":true,"range":14,"channel":1})";
        const std::string radio2 =
            R"({"id":"radio2","op":"game_radio","a":2,"enabled":true,"range":14,"channel":1})";
        check(api.invoke(api.send,radio1)=="1","Radio 1 queue rejected");
        check(waitReply(api,"radio1").find("\"ok\":true")!=std::string::npos,"Radio 1 rejected");
        check(api.invoke(api.send,radio2)=="1","Radio 2 queue rejected");
        check(waitReply(api,"radio2").find("\"ok\":true")!=std::string::npos,"Radio 2 rejected");

        auto send=R"({"id":"pkt01","op":"packet_send","requestId":"dm-request-42","a":1,"b":2,"bytes":96,"trafficClass":1,"timeoutTicks":30})";
        check(api.invoke(api.send,send)=="1","Packet submit queue rejected");
        const auto accepted=waitReply(api,"pkt01");
        check(accepted.find("\"accepted\":true")!=std::string::npos &&
              accepted.find("\"requestId\":\"dm-request-42\"")!=std::string::npos,
              "Packet not accepted by production transport");
        check(accepted.find("\"streamId\":")!=std::string::npos,
              "Submission missing transport epoch");

        bool delivered=false;
        for (int i=0;i<80 && !delivered;i++) {
            std::string cursor = "{\"id\":\"receipts" + std::to_string(i) +
                "\",\"op\":\"packet_receipts\",\"afterSequence\":0,\"limit\":16}";
            check(api.invoke(api.send,cursor)=="1","Receipt poll queue rejected");
            auto response=waitReply(api,"receipts"+std::to_string(i));
            if (response.find("\"requestId\":\"dm-request-42\"")!=std::string::npos &&
                response.find("\"status\":0")!=std::string::npos)
                delivered=true;
            std::this_thread::sleep_for(std::chrono::milliseconds(30));
        }
        check(delivered,"No correlated delivered receipt from x64 game transport");

        const auto stats=R"({"id":"stats1","op":"game_stats"})";
        check(api.invoke(api.send,stats)=="1","Status request enqueue failed");
        auto response=waitReply(api,"stats1");
        check(response.find("\"nodes\":2")!=std::string::npos,
              "Expected independent two-node game graph");
        check(response.find("\"streamId\":")!=std::string::npos,
              "Missing game transport instance ID");

        std::cout << "x86 BYOND string ABI -> named pipe -> x64 game engine"
                     " -> terminal receipt: PASS\n";
        return 0;
    } catch (const std::exception& ex) {
        std::cerr << "BYOND x86 bridge test FAILED: " << ex.what() << "\n";
        return 1;
    }
}
