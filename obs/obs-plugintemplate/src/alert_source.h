#pragma once
#include <vector>
#include <cstdint>

void alert_source_register();
void alert_source_enqueue_v2(const std::vector<uint8_t>& payload);
