#pragma once
#include <vector>
#include <cstdint>

// "Streaming Now Playing" OBS source — album art + title/artist with a customisable font.
void music_source_register();

// Pipe dispatch (called from plugin-main):
void music_source_set_now_playing(const std::vector<uint8_t>& payload); // MusicNowPlaying
void music_source_apply_settings(const std::vector<uint8_t>& payload);  // MusicNowPlayingSettings
