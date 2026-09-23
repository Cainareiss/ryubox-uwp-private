#pragma once
// Minimal boost/asio.hpp shim for DsuCompanionService
// DSU companion is not used on Xbox, so we stub the types.
#include <string>
#include <functional>
#include <cstdint>

namespace boost {
namespace asio {

class io_context {
public:
    void run() {}
    void stop() {}
    void restart() {}
    std::size_t poll() { return 0; }
};

namespace ip {

class address {
public:
    static address from_string(const std::string&) { return {}; }
    std::string to_string() const { return "0.0.0.0"; }
};

class udp {
public:
    class endpoint {
    public:
        endpoint() = default;
        endpoint(const address&, unsigned short) {}
        address address() const { return ip::address(); }
        unsigned short port() const { return 0; }
    };
    class socket {
    public:
        socket(io_context&) {}
        socket(io_context&, const endpoint&) {}
        void open(const udp&) {}
        void bind(const endpoint&) {}
        void close() {}
        bool is_open() const { return false; }
        template<typename B, typename H>
        void async_receive_from(B, endpoint&, H) {}
        template<typename B>
        std::size_t send_to(B, const endpoint&) { return 0; }
    };
    static udp v4() { return {}; }
};

} // namespace ip

template<typename T>
T buffer(const void*, std::size_t) { return T{}; }

inline auto buffer(const std::string& s) { return s; }
inline auto buffer(void* p, std::size_t) { return std::string(); }

namespace error {
inline const int operation_aborted = 0;
} // namespace error

using error_code = int;

} // namespace asio
} // namespace boost
