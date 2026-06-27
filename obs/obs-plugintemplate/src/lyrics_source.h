#pragma once
#include <vector>
#include <cstdint>

// "Streaming Lyrics" OBS source — multi-line karaoke with the active line highlighted.
void lyrics_source_register();

// Pipe dispatch (called from plugin-main):
void lyrics_source_set_lyrics(const std::vector<uint8_t>& payload);   // MusicLyrics
void lyrics_source_set_position(const std::vector<uint8_t>& payload); // MusicPosition
void lyrics_source_apply_settings(const std::vector<uint8_t>& payload); // MusicLyricsSettings
