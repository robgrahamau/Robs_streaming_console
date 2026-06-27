#include "pipe_client.h"
#include <obs-module.h>
#include <cstring>
#include <mutex>

PipeClient::PipeClient(const std::string& pipeName)
    : m_pipeName(pipeName)
{}

PipeClient::~PipeClient()
{
    stop();
}

void PipeClient::start()
{
    m_running = true;
    m_thread  = std::thread(&PipeClient::threadProc, this);
}

void PipeClient::stop()
{
    m_running   = false;
    m_connected = false;

    if (m_thread.joinable()) {
        // Abort any blocking ReadFile/CreateFile on the worker thread so it
        // exits its loop promptly.  The thread is the sole owner of m_pipe
        // and closes it itself — we must NOT also call CloseHandle here or we
        // get a double-close race.
        CancelSynchronousIo(m_thread.native_handle());
        m_thread.join();
    }
}

// Background thread: connect, read messages, reconnect on drop — forever.
void PipeClient::threadProc()
{
    while (m_running.load()) {

        if (!tryConnect()) {
            // App not running yet — wait and retry
            for (int i = 0; i < 50 && m_running.load(); i++)
                Sleep(100); // 5 s total, interruptible
            continue;
        }

        blog(LOG_INFO, "[Steaming] Connected to C# pipe.");
        if (m_connectedCallback)
            m_connectedCallback();

        // Read messages until the connection drops
        while (m_running.load() && m_connected.load()) {
            uint8_t header[5] = {};
            if (!readExact(header, 5)) break;

            auto type      = static_cast<PipeMessageType>(header[0]);
            uint32_t plen  = 0;
            std::memcpy(&plen, header + 1, 4);

            PipeMessage msg;
            msg.type = type;
            msg.payload.resize(plen);
            if (plen > 0 && !readExact(msg.payload.data(), plen)) break;

            if (type == PipeMessageType::Ping) {
                // Reply with Pong
                uint8_t pong[5] = { (uint8_t)PipeMessageType::Pong, 0, 0, 0, 0 };
                sendBytes(pong, sizeof(pong));
                continue;
            }

            if (m_callback)
                m_callback(msg);
        }

        // Connection dropped
        m_connected = false;
        if (m_pipe != INVALID_HANDLE_VALUE) {
            CloseHandle(m_pipe);
            m_pipe = INVALID_HANDLE_VALUE;
        }
        blog(LOG_INFO, "[Steaming] Pipe disconnected; will retry in 5 s.");

        for (int i = 0; i < 50 && m_running.load(); i++)
            Sleep(100);
    }
}

bool PipeClient::tryConnect()
{
    m_pipe = CreateFileA(
        m_pipeName.c_str(),
        GENERIC_READ | GENERIC_WRITE,
        0, nullptr,
        OPEN_EXISTING,
        0, nullptr
    );
    if (m_pipe == INVALID_HANDLE_VALUE)
        return false;

    DWORD mode = PIPE_READMODE_BYTE;
    SetNamedPipeHandleState(m_pipe, &mode, nullptr, nullptr);
    m_connected = true;
    return true;
}

bool PipeClient::readExact(void* buf, DWORD len)
{
    DWORD total = 0;
    while (total < len) {
        DWORD read = 0;
        if (!ReadFile(m_pipe, static_cast<uint8_t*>(buf) + total, len - total, &read, nullptr) || read == 0) {
            m_connected = false;
            return false;
        }
        total += read;
    }
    return true;
}

bool PipeClient::sendBytes(const void* buf, DWORD len)
{
    DWORD written = 0;
    return WriteFile(m_pipe, buf, len, &written, nullptr) && written == len;
}

bool PipeClient::sendMessage(PipeMessageType type, const std::vector<uint8_t>& payload)
{
    if (!m_connected.load() || m_pipe == INVALID_HANDLE_VALUE)
        return false;

    std::lock_guard<std::mutex> lock(m_sendMutex);

    if (!m_connected.load() || m_pipe == INVALID_HANDLE_VALUE)
        return false;

    std::vector<uint8_t> packet(5 + payload.size());
    packet[0] = static_cast<uint8_t>(type);
    uint32_t length = static_cast<uint32_t>(payload.size());
    std::memcpy(packet.data() + 1, &length, sizeof(length));
    if (!payload.empty())
        std::memcpy(packet.data() + 5, payload.data(), payload.size());
    return sendBytes(packet.data(), static_cast<DWORD>(packet.size()));
}
