#pragma once
#include <vector>
#include <cstdint>
#include <string>

void emoji_rain_source_register();

// Called by plugin-main when TriggerEmojiRain arrives.
// Payload: [2+N]emojis_utf8 [1]count
void emoji_rain_trigger(const std::vector<uint8_t>& payload);

// Called by plugin-main when EmojiRainSettings arrives.
// Payload: [1]emojiSize [2]fallSpeed [1]particleLifeSec [1]maxParticles [1]spread [1]fadeOut [1]spin
void emoji_rain_apply_settings(const std::vector<uint8_t>& payload);
