using System;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface IAudioRouteTransitionProcessor
{
    bool ProcessTransition(TranslationRoomAudioRoute route, AudioRoutingEventType eventType, string? payloadJson = null);
}
