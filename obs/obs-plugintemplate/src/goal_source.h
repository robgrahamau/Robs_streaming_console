#pragma once
#include <vector>
#include <cstdint>

void goal_source_register();

// Called by plugin-main when SetGoalLayout arrives.
// Payload: [1]goalIndex [N]ALT3_bytes
void goal_source_set_layout(const std::vector<uint8_t>& payload);

// Called by plugin-main when SetGoalNames arrives.
// Payload: [2]count [2+N]name0_utf8 [2+N]name1_utf8 ...
// Stores names and refreshes the goal_type property list on all instances.
void goal_source_set_names(const std::vector<uint8_t>& payload);
