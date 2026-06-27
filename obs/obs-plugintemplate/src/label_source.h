#pragma once
#include <vector>
#include <cstdint>

// LabelType values — must match C# LabelType enum
enum class LabelType : uint8_t {
    RecentFollower   = 0,
    RecentSubscriber = 1,
    SubscriberCount  = 2,
    ViewerCount      = 3,
    FollowerCount    = 4,
    StreamUptime     = 5,
    RecentDonation   = 6,
    TopDonation      = 7,
    DonationTotal    = 8,
    RecentGiftSub    = 9,
};

void label_source_register();

// Called by plugin-main when SetLabelLayout arrives.
// Payload: [1]labelType [N]ALT3_bytes
void label_source_set_layout(const std::vector<uint8_t>& payload);
