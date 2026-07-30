# Contract: translation_room.proto (Additions)

The following RPCs must be added to the TranslationRoomService to allow the TranscriptService to validate user permissions and room details.

```protobuf
syntax = "proto3";
package warptalk.translation_room;

// Existing GetTranslationRoom service/rpc...
// service TranslationRoomGrpc {
//   rpc GetTranslationRoom (GetTranslationRoomRequest) returns (GetTranslationRoomResponse);
//   ...

// New RPC for TranscriptService to check participants
rpc GetParticipantsByRoomId (GetParticipantsByRoomIdRequest) returns (GetParticipantsByRoomIdResponse);

message GetParticipantsByRoomIdRequest {
  string room_id = 1;
}

message GetParticipantsByRoomIdResponse {
  repeated Participant participants = 1;
}

message Participant {
  string id = 1;
  string display_name = 2;
  string role = 3;       // HOST, PARTICIPANT
  string language = 4;   // e.g. "vi-VN"
  bool is_active = 5;
}

// Ensure the existing GetTranslationRoomResponse includes workspace_id and created_by 
// for billing and ownership checks
// message GetTranslationRoomResponse {
//   ...
//   string workspace_id = 10;
//   string created_by = 11;
// }
```
