#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mfapi.h>
#include <obs-module.h>
#include "pipe_client.h"
#include "alert_source.h"
#include "chat_source.h"
#include "label_source.h"
#include "goal_source.h"
#include "emoji_rain_source.h"
#include "music_source.h"
#include "lyrics_source.h"
#include <string>
#include <vector>
#pragma comment(lib, "mfplat.lib")

#ifndef STEAMING_PLUGIN_VERSION
#define STEAMING_PLUGIN_VERSION "0.1.0"
#endif

namespace {
constexpr int kPipeProtocolVersion = 1;

std::vector<uint8_t> build_plugin_hello_payload()
{
    std::string json =
        std::string("{\"role\":\"plugin\",\"protocolVersion\":") + std::to_string(kPipeProtocolVersion) +
        ",\"version\":\"" + STEAMING_PLUGIN_VERSION +
        "\",\"capabilities\":[\"render_alert_v2\",\"render_chat\",\"label_layouts\",\"goal_layouts\",\"emoji_rain\",\"chat_source_list\",\"music\"]}";
    return std::vector<uint8_t>(json.begin(), json.end());
}
}

OBS_DECLARE_MODULE()
OBS_MODULE_USE_DEFAULT_LOCALE("steaming-plugin", "en-US")

static PipeClient* g_pipe = nullptr;

bool obs_module_load(void)
{
    blog(LOG_INFO, "[Steaming] Plugin loading...");
    MFStartup(MF_VERSION, MFSTARTUP_LITE);

    alert_source_register();
    chat_source_register();
    label_source_register();
    goal_source_register();
    emoji_rain_source_register();
    music_source_register();
    lyrics_source_register();

    g_pipe = new PipeClient("\\\\.\\pipe\\steaming");
    g_pipe->setConnectedCallback([]() {
        g_pipe->sendMessage(PipeMessageType::Hello, build_plugin_hello_payload());
        chat_source_send_source_list();
    });
    g_pipe->setMessageCallback([](const PipeMessage& msg) {
        switch (msg.type) {
            case PipeMessageType::Hello:
                blog(LOG_INFO, "[Steaming] Received app hello (%zu bytes).", msg.payload.size());
                break;
            case PipeMessageType::RenderAlert:
                // Legacy v1 kept for compatibility; not sent by current C# app.
                blog(LOG_INFO, "[Steaming] RenderAlert v1 (%zu bytes) ignored; use V2.", msg.payload.size());
                break;
            case PipeMessageType::RenderAlertV2:
                blog(LOG_INFO, "[Steaming] RenderAlertV2 (%zu bytes).", msg.payload.size());
                alert_source_enqueue_v2(msg.payload);
                break;
            case PipeMessageType::RenderChat:
                chat_source_enqueue(msg.payload);
                break;
            case PipeMessageType::Clear:
                chat_source_clear();
                break;
            case PipeMessageType::RefreshChat:
                // Emote/badge image downloaded; mark all chat sources dirty.
                chat_source_mark_dirty();
                break;
            case PipeMessageType::UpdateChatSettings:
                chat_source_apply_settings(msg.payload);
                break;
            case PipeMessageType::SetLabelLayout:
                label_source_set_layout(msg.payload);
                break;
            case PipeMessageType::SetGoalLayout:
                goal_source_set_layout(msg.payload);
                break;
            case PipeMessageType::TriggerEmojiRain:
                emoji_rain_trigger(msg.payload);
                break;
            case PipeMessageType::EmojiRainSettings:
                emoji_rain_apply_settings(msg.payload);
                break;
            case PipeMessageType::SetGoalNames:
                goal_source_set_names(msg.payload);
                break;
            case PipeMessageType::MusicNowPlaying:
                music_source_set_now_playing(msg.payload);
                break;
            case PipeMessageType::MusicPosition:
                lyrics_source_set_position(msg.payload);
                break;
            case PipeMessageType::MusicLyrics:
                lyrics_source_set_lyrics(msg.payload);
                break;
            case PipeMessageType::MusicNowPlayingSettings:
                music_source_apply_settings(msg.payload);
                break;
            case PipeMessageType::MusicLyricsSettings:
                lyrics_source_apply_settings(msg.payload);
                break;
            default:
                break;
        }
    });
    g_pipe->start();

    blog(LOG_INFO, "[Steaming] Plugin loaded v%s - pipe auto-reconnect active.", STEAMING_PLUGIN_VERSION);
    return true;
}

void obs_module_unload(void)
{
    blog(LOG_INFO, "[Steaming] Plugin unloading.");
    if (g_pipe) {
        g_pipe->stop();
        delete g_pipe;
        g_pipe = nullptr;
    }
    MFShutdown();
}

PipeClient* steaming_get_pipe() { return g_pipe; }
