import os

def replace_in_file(path, old, new):
    if not os.path.exists(path):
        return
    with open(path, 'r') as f:
        content = f.read()
    content = content.replace(old, new)
    with open(path, 'w') as f:
        f.write(content)

# TranslationRoomParticipantMapper.cs
p_mapper = "translation-room/src/WarpTalk.TranslationRoomService.Application/Mappers/TranslationRoomParticipantMapper.cs"
replace_in_file(p_mapper, "Role: participant.Role,", "Role: participant.Role.ToString(),")
replace_in_file(p_mapper, "Role: participant.Role.ToString().ToString(),", "Role: participant.Role.ToString(),")

# ArtifactMapper.cs
a_mapper = "translation-room/src/WarpTalk.TranslationRoomService.Application/Mappers/ArtifactMapper.cs"
replace_in_file(a_mapper, "ArtifactType: artifact.ArtifactType,", "ArtifactType: artifact.ArtifactType.ToString(),")
replace_in_file(a_mapper, "ArtifactType: artifact.ArtifactType.ToString().ToString(),", "ArtifactType: artifact.ArtifactType.ToString(),")

# TranslationRoomAudioRouteMapper.cs
ar_mapper = "translation-room/src/WarpTalk.TranslationRoomService.Application/Mappers/TranslationRoomAudioRouteMapper.cs"
replace_in_file(ar_mapper, "Status: route.Status,", "Status: route.Status.ToString(),")
replace_in_file(ar_mapper, "Status: route.Status.ToString().ToString(),", "Status: route.Status.ToString(),")

# TranslationRoomMapper.cs
tr_mapper = "translation-room/src/WarpTalk.TranslationRoomService.Application/Mappers/TranslationRoomMapper.cs"
replace_in_file(tr_mapper, "TranslationRoomType: room.TranslationRoomType,", "TranslationRoomType: room.TranslationRoomType.ToString(),")
replace_in_file(tr_mapper, "TranslationRoomType: room.TranslationRoomType.ToString().ToString(),", "TranslationRoomType: room.TranslationRoomType.ToString(),")

replace_in_file(tr_mapper, "TranslationRoomType: room.TranslationRoomType,", "TranslationRoomType: room.TranslationRoomType.ToString(),") # double check

replace_in_file(tr_mapper, "ArtifactAccess = roomSettings.ArtifactAccess", "ArtifactAccess = roomSettings.ArtifactAccess.ToString()")
replace_in_file(tr_mapper, "ArtifactAccess = roomSettings.ArtifactAccess.ToString().ToString()", "ArtifactAccess = roomSettings.ArtifactAccess.ToString()")


# TranslationRoomAudioRouteService.cs
ar_service = "translation-room/src/WarpTalk.TranslationRoomService.Application/Services/TranslationRoomAudioRouteService.cs"
replace_in_file(ar_service, "request.Status.HasValue", "request.Status != null")
replace_in_file(ar_service, "request.Status.Value.ToString()", "request.Status")
replace_in_file(ar_service, "request.Status.Value", "request.Status")


# TranslationRoomService.cs
tr_service = "translation-room/src/WarpTalk.TranslationRoomService.Application/Services/TranslationRoomService.cs"
replace_in_file(tr_service, "ArtifactAccess = request.Settings?.ArtifactAccess ?? ArtifactAccessLevel.HostOnly,", "ArtifactAccess = request.Settings?.ArtifactAccess ?? ArtifactAccessLevel.HostOnly.ToString(),")
replace_in_file(tr_service, "ArtifactAccess = settingsObj?.ArtifactAccess ?? ArtifactAccessLevel.HostOnly", "ArtifactAccess = settingsObj?.ArtifactAccess.ToString() ?? ArtifactAccessLevel.HostOnly.ToString()")
replace_in_file(tr_service, "ArtifactAccess = request.Settings?.ArtifactAccess ?? ArtifactAccessLevel.HostOnly.ToString()", "ArtifactAccess = request.Settings?.ArtifactAccess ?? ArtifactAccessLevel.HostOnly.ToString()")

