namespace Steaming.Core.Platforms;

public sealed record KickBridgeSessionBootstrap(
    string AccessToken,
    string Username,
    int BroadcasterUserId,
    int ChatroomId,
    bool AllowOutboundChat);
