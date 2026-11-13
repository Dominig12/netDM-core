#pragma once

#include <atomic>
#include <chrono>
#include <cstdlib>
#include <string>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <cerrno>
#include <cstring>
#include <poll.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>
#endif

// Used only by the worker. Read returns 0 when no bytes are available, -1 on
// disconnect/error, and a positive count otherwise.
class LocalStream {
public:
    LocalStream() = default;
    LocalStream(const LocalStream&) = delete;
    LocalStream& operator=(const LocalStream&) = delete;

    bool open() {
#ifdef _WIN32
        const char* configured = std::getenv("NETDM_PIPE_NAME");
        const std::string name = configured ? configured : "netdm-core";
        if (name.empty() || name.size() > 80 || name.find_first_not_of(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") != std::string::npos)
            return false;
        const auto path = R"(\\.\pipe\)" + name;
        pipe_ = CreateFileA(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
            OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION,
            nullptr);
        if (pipe_ == INVALID_HANDLE_VALUE) return false;
        event_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        return event_ != nullptr;
#else
        const char* configured = std::getenv("NETDM_SOCKET_PATH");
        const char* path = configured ? configured : "/run/netdm/core.sock";
        sockaddr_un address{};
        address.sun_family = AF_UNIX;
        const auto length = std::strlen(path);
        if (path[0] != '/' || length >= sizeof(address.sun_path)) return false;
        std::memcpy(address.sun_path, path, length + 1);
        socket_ = ::socket(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
        return socket_ >= 0 && ::connect(socket_,
            reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == 0;
#endif
    }

    ~LocalStream() {
#ifdef _WIN32
        if (pipe_ != INVALID_HANDLE_VALUE) CloseHandle(pipe_);
        if (event_) CloseHandle(event_);
#else
        if (socket_ >= 0) ::close(socket_);
#endif
    }

    bool write(const std::string& data, const std::atomic<bool>& stop) {
        const auto deadline = Clock::now() + std::chrono::seconds(2);
        size_t sent = 0;
        while (sent < data.size() && !stop.load() && Clock::now() < deadline) {
#ifdef _WIN32
            OVERLAPPED operation{};
            operation.hEvent = event_;
            ResetEvent(event_);
            DWORD count = 0;
            if (!WriteFile(pipe_, data.data() + sent, static_cast<DWORD>(data.size() - sent),
                    &count, &operation) && !complete(operation, count, stop, deadline)) return false;
#else
            const auto count = ::send(socket_, data.data() + sent, data.size() - sent, MSG_NOSIGNAL);
            if (count < 0) {
                if (errno == EINTR) continue;
                if (errno != EAGAIN && errno != EWOULDBLOCK) return false;
                pollfd ready{socket_, POLLOUT, 0};
                if (::poll(&ready, 1, 25) < 0 && errno != EINTR) return false;
                continue;
            }
#endif
            if (count == 0) return false;
            sent += count;
        }
        return sent == data.size();
    }

    int read(char* data, size_t capacity, const std::atomic<bool>& stop) {
#ifdef _WIN32
        DWORD available = 0;
        if (!PeekNamedPipe(pipe_, nullptr, 0, nullptr, &available, nullptr)) return -1;
        if (available == 0) return 0;
        OVERLAPPED operation{};
        operation.hEvent = event_;
        ResetEvent(event_);
        DWORD count = 0;
        const auto size = available < capacity ? available : static_cast<DWORD>(capacity);
        if (!ReadFile(pipe_, data, size, &count, &operation) &&
            !complete(operation, count, stop, Clock::now() + std::chrono::seconds(2))) return -1;
        return count ? static_cast<int>(count) : -1;
#else
        (void)stop;
        const auto count = ::recv(socket_, data, capacity, 0);
        if (count < 0 && (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR)) return 0;
        return count > 0 ? static_cast<int>(count) : -1;
#endif
    }

private:
    using Clock = std::chrono::steady_clock;
#ifdef _WIN32
    HANDLE pipe_{INVALID_HANDLE_VALUE};
    HANDLE event_{nullptr};

    bool complete(OVERLAPPED& operation, DWORD& count, const std::atomic<bool>& stop,
            Clock::time_point deadline) {
        if (GetLastError() != ERROR_IO_PENDING) return false;
        while (!stop.load() && Clock::now() < deadline) {
            const auto result = WaitForSingleObject(event_, 25);
            if (result == WAIT_OBJECT_0)
                return GetOverlappedResult(pipe_, &operation, &count, FALSE) != FALSE;
            if (result != WAIT_TIMEOUT) break;
        }
        CancelIoEx(pipe_, &operation);
        // Keep the OVERLAPPED and I/O buffer alive until cancellation completes.
        GetOverlappedResult(pipe_, &operation, &count, TRUE);
        return false;
    }
#else
    int socket_{-1};
#endif
};
