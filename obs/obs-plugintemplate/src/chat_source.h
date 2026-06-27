#pragma once
#include <vector>
#include <cstdint>

void chat_source_register();

// Called by plugin-main when a RenderChat pipe message arrives.
void chat_source_enqueue(const std::vector<uint8_t>& payload);

// Called when an emote/badge image finishes downloading — marks all chat
// source instances dirty so they redraw with the newly available image.
void chat_source_mark_dirty();

// Clears the global OBS chat buffer and marks all chat source instances dirty.
void chat_source_clear();

// Called by plugin-main when C# sends updated chat overlay settings.
void chat_source_apply_settings(const std::vector<uint8_t>& payload);

// Sends the current list of live steaming_chat source names back to the app.
void chat_source_send_source_list();
