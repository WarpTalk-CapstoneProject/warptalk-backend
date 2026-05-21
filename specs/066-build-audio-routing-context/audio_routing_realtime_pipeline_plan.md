# Audio Routing Realtime Pipeline — Implementation Plan

## 1. Tổng quan (Overview)

Tài liệu này mô tả kiến trúc kỹ thuật đầy đủ và kế hoạch triển khai để hệ thống WarpTalk có thể **handle toàn bộ 10 trạng thái (status)** của Audio Routing theo State Machine đã được thiết kế, bao gồm cả luồng liên lạc hai chiều giữa:
- **`warptalk-ai` (Python Workers)** — đo lường, phát hiện sự cố, thực thi lệnh
- **`warptalk-backend / translation-room` (C# .NET)** — State Machine, persistence, điều phối

---

## 2. Kiến trúc tổng thể (Architecture)

```
┌─────────────────────────────────────────────────────────────────┐
│                        CLIENT (Web/Mobile)                       │
│           Nhận cảnh báo UI qua SignalR (Gateway Hub)            │
└─────────────────────────────────┬───────────────────────────────┘
                                  │ SignalR
                                  ▼
┌──────────────────────────────────────────────────────────────────┐
│                    GATEWAY (C# .NET)                             │
│  - AiResultConsumerService: forward STT/TTS → Client            │
│  - NotificationRedisSubscriberService: push route status → Client│
└──────────────────────┬─────────────────────┬────────────────────┘
                       │ Redis Pub/Sub        │ Redis Stream
                       ▼                     ▼
┌──────────────────────────────────────────────────────────────────┐
│              TRANSLATION ROOM SERVICE (C# .NET)                  │
│                                                                  │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │  TranslationRoomEventConsumerService (BackgroundService) │    │
│  │  Đọc: translationRoom:system_events (Redis Stream)      │    │
│  └──────────────────────────┬──────────────────────────────┘    │
│                             │                                    │
│                             ▼                                    │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │  AudioRouteEventProcessorService                         │    │
│  │  Gọi AudioRouteStateMachine → Update DB → Pub/Sub       │    │
│  └─────────────────────────────────────────────────────────┘    │
│                                                                  │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │  AudioRouteStateMachine (Domain)                         │    │
│  │  10 trạng thái, deterministic transitions                │    │
│  └─────────────────────────────────────────────────────────┘    │
└──────────────────────┬──────────────────────────────────────────┘
                       │ Pub/Sub: translationRoom:{id}:route_updated
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│                  REDIS (Message Broker)                          │
│                                                                  │
│  Streams:                                                        │
│    audio:chunks:{roomId}         ← Client audio input           │
│    stt:results:{roomId}          → Gateway → Client             │
│    translate:results:{roomId}    → Gateway → Client             │
│    tts:results:{roomId}          → Gateway → Client             │
│    ai_assistant:results:{roomId} → Gateway → Client             │
│    translationRoom:system_events ← AI Workers phát hiện sự cố  │
│                                                                  │
│  Pub/Sub:                                                        │
│    translationRoom:{id}:route_updated  → Gateway & AI Workers   │
└──────────────────────┬──────────────────────────────────────────┘
                       │ Stream & Pub/Sub
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│                  WARPTALK-AI (Python Workers)                    │
│                                                                  │
│  stt_worker      → đo STT latency, publish system_events        │
│  translation_worker → đo MT latency, publish system_events      │
│  tts_worker      → detect voice unavailable, publish events     │
│  ai_assistant_worker → tóm tắt cuộc họp, publish to Redis      │
│                                                                  │
│  Tất cả subscribe: translationRoom:{id}:route_updated           │
│  → Tự adapt model khi status thay đổi                          │
└──────────────────────────────────────────────────────────────────┘
```

---

## 3. State Machine — 10 Trạng thái và Sự kiện

### 3.1 Bảng trạng thái

| # | Status | Ý nghĩa | Ai chịu trách nhiệm |
|---|--------|---------|---------------------|
| 1 | `IDLE` | Route chưa được kích hoạt, phòng chưa bắt đầu | Backend (khởi tạo khi tạo phòng) |
| 2 | `ROUTING_READY` | Participants & languages đã cấu hình, sẵn sàng | Backend (sau khi join đủ thành phần) |
| 3 | `AUDIO_ROUTING_ACTIVE` | AI pipeline đang chạy full công suất | Backend (khi host start) |
| 4 | `TRANSLATION_DEGRADED` | STT hoặc MT latency cao, chất lượng dịch giảm | AI Worker (phát hiện) → Backend (đổi state) |
| 5 | `VOICE_OUTPUT_DEGRADED` | TTS hoặc Voice Clone gặp sự cố | AI Worker (phát hiện) → Backend (đổi state) |
| 6 | `TEXT_ONLY_MODE` | Audio output hoàn toàn không khả dụng | AI Worker (phát hiện) → Backend (đổi state) |
| 7 | `PAUSED` | Host tạm dừng phiên, pipeline dừng xử lý | Backend (khi host pause) |
| 8 | `STOPPING` | Host kết thúc phiên, đang flush dữ liệu | Backend (khi host end) |
| 9 | `FINALIZING_ARTIFACTS` | Đang xử lý transcript, recording, summary | Backend hoặc AI Worker (artifacts job) |
| 10 | `COMPLETED` | Toàn bộ hoàn tất, terminal state | Backend (sau khi artifacts linked) |

### 3.2 Phân loại và Định nghĩa Sự kiện (Event Definitions by Actor)

Để State Machine hoạt động chuẩn xác, mọi sự kiện chuyển đổi trạng thái phải được định nghĩa rõ ràng theo từng tác nhân (Actor/Source) và hành động cụ thể (Action):

#### A. Nhóm Sự kiện do User (Người dùng) chủ động kích hoạt (User Actions)
Các sự kiện này phát sinh trực tiếp từ thao tác bấm nút của Host trên giao diện UI (Web/Mobile Client), gửi API request lên Backend:

| Sự kiện (Event) | Hành động kích hoạt (Triggering User Action) | Trạng thái bắt đầu | Trạng thái đích |
|:---|:---|:---:|:---:|
| `host_pauses_session` | Host click nút **Tạm dừng** (Pause) phiên dịch trên UI. | `AUDIO_ROUTING_ACTIVE`<br/>`TRANSLATION_DEGRADED`<br/>`VOICE_OUTPUT_DEGRADED`<br/>`TEXT_ONLY_MODE` | `PAUSED` |
| `host_resumes_session` | Host click nút **Tiếp tục** (Resume) phiên dịch trên UI. | `PAUSED` | `AUDIO_ROUTING_ACTIVE` |
| `host_ends_session` | Host click nút **Kết thúc** (End Session) phòng họp trên UI. | Bất kỳ trạng thái nào (trừ terminal `COMPLETED`) | `STOPPING` |

#### B. Nhóm Sự kiện do Hệ thống tính toán (C# Backend / Telemetry Actions)
Các sự kiện này được kích hoạt tự động bởi thuật toán nội bộ của Backend C# (EMA, Hysteresis, Background Jobs, Room Controls):

| Sự kiện (Event) | Hành động kích hoạt (Triggering System Logic) | Trạng thái bắt đầu | Trạng thái đích |
|:---|:---|:---:|:---:|
| `participants_and_languages_configured` | Người dùng cấu hình xong vai trò, ngôn ngữ nguồn/đích của tất cả thành viên trong phòng. | `IDLE` | `ROUTING_READY` |
| `session_starts` | Host bấm Start Session $\Rightarrow$ C# khởi tạo các Redis stream và cho phép định tuyến âm thanh. | `ROUTING_READY` | `AUDIO_ROUTING_ACTIVE` |
| `stt_or_translation_latency_high` | Bộ xử lý telemetry EMA tại Backend phát hiện độ trễ trượt STT/MT của phòng họp vượt ngưỡng **3000ms**. | `AUDIO_ROUTING_ACTIVE` | `TRANSLATION_DEGRADED` |
| `tts_latency_high` | Bộ xử lý telemetry EMA tại Backend phát hiện độ trễ trượt TTS của phòng họp vượt ngưỡng **6000ms**. | `AUDIO_ROUTING_ACTIVE` | `VOICE_OUTPUT_DEGRADED` |
| `pipeline_recovered` | Bộ xử lý telemetry EMA phát hiện độ trễ STT/MT giảm sâu xuống dưới ngưỡng **1500ms**. | `TRANSLATION_DEGRADED` | `AUDIO_ROUTING_ACTIVE` |
| `stop_routing_and_flush_data` | Background job phát hiện đã hoàn tất việc flush toàn bộ audio chunk còn sót trong hàng đợi (quá 30s timeout). | `STOPPING` | `FINALIZING_ARTIFACTS` |
| `transcript_recording_summary_linked` | Hệ thống hoàn tất việc liên kết Transcript, file ghi âm cuộc họp, và AI Summary thành công vào Database. | `FINALIZING_ARTIFACTS` | `COMPLETED` |

#### C. Nhóm Sự kiện do AI Workers phát hiện sự cố mô hình (AI Worker Actions)
Các sự kiện này phát sinh từ các Exception / Cảnh báo phần cứng hoặc tài nguyên VRAM trong quá trình AI Workers chạy inference:

| Sự kiện (Event) | Hành động kích hoạt (AI Worker Exception / Event) | Trạng thái bắt đầu | Trạng thái đích |
|:---|:---|:---:|:---:|
| `voice_clone_unavailable` | AI Worker chạy XTTS gặp lỗi OOM (CUDA OutOfMemory), thiếu mẫu giọng nói (<3s) hoặc model crash $\Rightarrow$ Chuyển sang giọng Edge-TTS mặc định. | `AUDIO_ROUTING_ACTIVE` | `VOICE_OUTPUT_DEGRADED` |
| `edge_tts_unavailable` | AI Worker không thể kết xuất được bất kỳ âm thanh nào (XTTS đã sập, **Edge-TTS cũng lỗi**) $\Rightarrow$ Cắt bỏ hoàn toàn luồng phát tiếng. | `AUDIO_ROUTING_ACTIVE` | `TEXT_ONLY_MODE` |
| `voice_recovered` | AI Worker khôi phục được mô hình XTTS v2, giải phóng đủ VRAM hoặc kết nối lại thành công dịch vụ Voice Clone. | `VOICE_OUTPUT_DEGRADED` | `AUDIO_ROUTING_ACTIVE` |

> [!NOTE]
> **Phân biệt với User Action (Tắt Audio Clone thủ công):**
> Khi người dùng hoặc Host tắt tính năng Voice Clone một cách chủ động thông qua nút bấm trên UI (kích hoạt API của `TranslationRoomAudioRouteService`), Backend sẽ cập nhật flag `VoiceCloneEnabled = false` trong DB và sync cache Redis. 
> Hành động này **không** làm chuyển trạng thái Route sang `VOICE_OUTPUT_DEGRADED` (Route vẫn giữ trạng thái chạy bình thường `AUDIO_ROUTING_ACTIVE` nhưng không clone giọng). Trạng thái `VOICE_OUTPUT_DEGRADED` chỉ được kích hoạt khi AI Worker gặp sự cố mô hình thực tế và phát ra telemetry event `voice_clone_unavailable`.

#### D. Nhóm Sự kiện do Thiết bị / Trình duyệt Client (Client Device / WebRTC Actions)
Các sự kiện này phát sinh từ lỗi thiết bị của người dùng ở frontend:

| Sự kiện (Event) | Hành động kích hoạt (Client UI/WebRTC Action) | Trạng thái bắt đầu | Trạng thái đích |
|:---|:---|:---:|:---:|
| `audio_output_unavailable` | Trình duyệt của client chặn tự động phát âm thanh (Autoplay block) hoặc thiết bị loa của client bị mất kết nối WebRTC. | `AUDIO_ROUTING_ACTIVE` | `TEXT_ONLY_MODE` |
| `audio_recovered` | Client cấp quyền autoplay thành công hoặc thiết bị âm thanh được cắm lại $\Rightarrow$ WebRTC kết nối lại thành công. | `TEXT_ONLY_MODE` | `AUDIO_ROUTING_ACTIVE` |

---

## 4. Kế hoạch triển khai (Implementation Tasks)

---

### PHASE 1 — Backend: Bổ sung Status & Event còn thiếu

#### 1.1 Cập nhật `AudioRouteStatus.cs` (DONE ✅)
- Thêm `PAUSED` vào enum.

#### 1.2 Cập nhật `AudioRoutingEventType.cs` (DONE ✅)
- Thêm `host_pauses_session`, `host_resumes_session` vào enum.

#### 1.3 Cập nhật `AudioRouteStateMachine.cs`
- Thêm transitions cho `PAUSED`:
  ```csharp
  AudioRouteStatus.PAUSED => eventType switch
  {
      AudioRoutingEventType.host_resumes_session => Result.Success(AudioRouteStatus.AUDIO_ROUTING_ACTIVE),
      _ => InvalidTransition(currentState, eventType)
  },
  ```
- Cập nhật priority override cho `host_ends_session` để bao gồm `PAUSED`.
- Cập nhật priority override cho `host_pauses_session` từ Active/Degraded states.

#### 1.4 Bổ sung trigger trong `TranslationRoomService.cs`
- `PauseTranslationRoomAsync` → trigger `host_pauses_session`.
- `ResumeTranslationRoomAsync` → trigger `host_resumes_session`.

#### 1.5 Implement Artifacts Finalization Job
- Khi Route chuyển sang `STOPPING`, backend auto-trigger một background job:
  - Flush Redis buffer (đợi các segment còn sót).
  - Trigger event `stop_routing_and_flush_data` → chuyển sang `FINALIZING_ARTIFACTS`.
  - Sau khi transcript, recording, summary được linked vào DB → trigger `transcript_recording_summary_linked` → `COMPLETED`.

---

### PHASE 2 — Backend: Harden Consumer Service

#### 2.1 Cải thiện `TranslationRoomEventConsumerService`
**Vấn đề hiện tại:** Consumer group hardcode `worker_1`, không hỗ trợ horizontal scaling.

**Cần làm:**
```csharp
// Sinh consumer name động (mỗi instance backend là 1 consumer riêng)
var consumerName = $"backend-{Environment.MachineName}-{Guid.NewGuid():N[..8]}";
```

#### 2.2 Thêm Dead Letter handling
- Nếu một event fail sau 3 lần retry, đẩy vào stream `translationRoom:system_events:dlq` để audit.

#### 2.3 Implement `PublishRoutesUpdateAsync` đầy đủ
- Sau mỗi state transition thành công, publish Pub/Sub `translationRoom:{roomId}:route_updated` với payload chứa:
  ```json
  {
    "roomId": "...",
    "routeId": "...",
    "oldStatus": "AUDIO_ROUTING_ACTIVE",
    "newStatus": "TRANSLATION_DEGRADED",
    "timestamp": "2026-05-17T..."
  }
  ```

---

### PHASE 3 — Centralized Telemetry & EMA Latency Tracking

Để giải quyết triệt để các nhược điểm của việc tính độ trễ cục bộ tại Worker (bị flap ở ranh giới, mất state khi scaling worker, không đo được hàng đợi), hệ thống áp dụng cơ chế **Centralized Telemetry (Giám sát tập trung)**.

---

#### 3.1 Đo lường End-to-End Latency tại AI Workers (Python)

Các AI Workers (STT, TTS, MT/Translation) chỉ đóng vai trò làm cảm biến. Khi xử lý xong mỗi audio chunk hoặc text chunk, Worker sẽ so sánh **thời điểm hiện tại** với **thời điểm bắt đầu tạo ra audio chunk đó** (lấy từ Redis Message ID hoặc trường `created_at` trong payload) để tính độ trễ toàn trình (bao gồm cả hàng đợi).

**Công thức tại `stt_worker/worker.py` / `tts_worker/worker.py`:**
```python
import time
import json

async def process(self, message_id, data):
    # 1. Thực thi model (Inference)
    # Whisper hoặc XTTS synthesis...
    
    # 2. Tính toán End-to-End Latency
    # Lấy timestamp gốc từ message_id Redis (dạng "timestamp_ms-sequence")
    creation_timestamp_ms = int(message_id.decode('utf-8').split('-')[0])
    current_timestamp_ms = int(time.time() * 1000)
    e2e_latency_ms = current_timestamp_ms - creation_timestamp_ms
    
    # 3. Publish Raw Telemetry về C# Backend
    await self.redis_client.publish(
        "translationRoom:telemetry",
        json.dumps({
            "roomId": data["room_id"],
            "workerType": self.worker_name, # "stt" hoặc "tts"
            "latencyMs": e2e_latency_ms,
            "timestamp": current_timestamp_ms
        })
    )
```

---

#### 3.2 Bộ xử lý Toán học EMA & Hysteresis tại C# Backend

C# Backend chạy một `TelemetryProcessorService` để nhận dữ liệu từ kênh Redis `translationRoom:telemetry`, áp dụng công thức **EMA thích ứng (Adaptive EMA)** và dải **Hysteresis** chống flap trạng thái. 

Để đáp ứng mở rộng quy mô (Horizontal Scaling) mà không gây bất đồng bộ dữ liệu, **trạng thái telemetry của phòng họp được lưu trữ tập trung tại Redis Hash** thay vì RAM cục bộ.

##### A. Công thức Adaptive EMA (Hệ số mượt thích ứng)
> $EMA_{new} = (Latency_{current} \times \alpha_{adaptive}) + (EMA_{old} \times (1 - \alpha_{adaptive}))$

Hệ số $\alpha_{adaptive}$ được điều chỉnh động dựa trên khoảng thời gian ($\Delta t$) giữa sự kiện telemetry hiện tại và sự kiện gần nhất trước đó:
- **Nếu $\Delta t < 1000\text{ ms}$ (Mật độ nói cực cao, tranh luận liên tục):** Hạ $\alpha$ xuống thấp (tối thiểu `0.1`) để tăng cường lọc nhiễu, tránh việc EMA giật cục bộ.
- **Nếu $1000\text{ ms} \le \Delta t \le 3000\text{ ms}$ (Tốc độ nói bình thường):** Giữ $\alpha$ ở mức tiêu chuẩn `0.3`.
- **Nếu $\Delta t > 3000\text{ ms}$ (Phòng họp im lặng một lúc mới nói lại):** Tăng $\alpha$ lên cao (lên tới `0.6`) để EMA nhạy bén hơn, lập tức ghi nhận độ trễ thực tế của chunk đầu tiên sau quãng lặng.

##### B. Cấu hình Ngưỡng (Thresholds) & Hysteresis kép
Do STT và TTS có thời gian chạy mô hình rất khác nhau, Backend áp dụng các tham số riêng biệt:

| Worker Type | Ngưỡng Degradation (Upper Bound) | Ngưỡng Recovery (Lower Bound) |
|---|---|---|
| **STT / Translation** | `> 3000ms` (Trễ STT/MT cao) | `< 1500ms` (Đường truyền mượt) |
| **TTS (Tổng hợp âm thanh)** | `> 6000ms` (Trễ giọng đọc cao) | `< 3000ms` (Đường truyền mượt) |

##### C. Triển khai trong C# (`TelemetryProcessorService.cs` với Redis State & Adaptive Alpha)
```csharp
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public class TelemetryProcessorService : ITelemetryProcessorService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IAudioRouteEventProcessorService _eventProcessor;
    
    // Ngưỡng cho STT
    private const double SttDegradedMs = 3000.0;
    private const double SttRecoveryMs = 1500.0;
    
    // Ngưỡng cho TTS
    private const double TtsDegradedMs = 6000.0;
    private const double TtsRecoveryMs = 3000.0;

    public TelemetryProcessorService(IConnectionMultiplexer redis, IAudioRouteEventProcessorService eventProcessor)
    {
        _redis = redis;
        _eventProcessor = eventProcessor;
    }

    public async Task ProcessTelemetryAsync(TelemetryPayload payload, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var hashKey = $"translationRoom:{payload.RoomId}:telemetry_state";

        // 1. Lấy trạng thái Telemetry tập trung từ Redis Hash (Tránh Split-brain khi scale ngang)
        var stateEntries = await db.HashGetAllAsync(hashKey);
        
        double oldSttEma = 0, oldTtsEma = 0;
        bool isSttDegraded = false, isTtsDegraded = false;
        int warmupCount = 0;
        long lastTimestamp = 0;

        foreach (var entry in stateEntries)
        {
            if (entry.Name == "stt_ema") oldSttEma = (double)entry.Value;
            else if (entry.Name == "tts_ema") oldTtsEma = (double)entry.Value;
            else if (entry.Name == "is_stt_degraded") isSttDegraded = (bool)entry.Value;
            else if (entry.Name == "is_tts_degraded") isTtsDegraded = (bool)entry.Value;
            else if (entry.Name == "warmup_count") warmupCount = (int)entry.Value;
            else if (entry.Name == "last_timestamp") lastTimestamp = (long)entry.Value;
        }

        // 2. Kiểm tra Warm-up (Bỏ qua 3 chunk đầu khi mới khởi động phòng để tránh alert giả)
        if (warmupCount < 3)
        {
            warmupCount++;
            await db.HashSetAsync(hashKey, new HashEntry[] { 
                new HashEntry("warmup_count", warmupCount),
                new HashEntry("last_timestamp", payload.Timestamp)
            });
            return;
        }

        // 3. Tính toán Hệ số mượt EMA thích ứng (Adaptive Alpha)
        double alpha = 0.3; // Default
        if (lastTimestamp > 0)
        {
            double deltaSec = (payload.Timestamp - lastTimestamp) / 1000.0;
            if (deltaSec < 1.0)
            {
                alpha = 0.1 + (0.2 * deltaSec); // Giảm mượt xuống tối thiểu 0.1 khi nói dồn dập
            }
            else if (deltaSec > 3.0)
            {
                alpha = Math.Min(0.6, 0.3 + (0.1 * (deltaSec - 3.0))); // Tăng nhạy lên tới 0.6 khi nói lại sau quãng lặng
            }
        }

        // 4. Áp dụng công thức tính toán EMA và đánh giá Hysteresis
        var updates = new List<HashEntry> { new HashEntry("last_timestamp", payload.Timestamp) };

        if (payload.WorkerType == "stt")
        {
            double newSttEma = oldSttEma == 0 ? payload.LatencyMs : (payload.LatencyMs * alpha) + (oldSttEma * (1 - alpha));
            updates.Add(new HashEntry("stt_ema", newSttEma));

            if (!isSttDegraded && newSttEma > SttDegradedMs)
            {
                isSttDegraded = true;
                updates.Add(new HashEntry("is_stt_degraded", true));
                _ = _eventProcessor.ProcessEventAsync(payload.RoomId, null, AudioRoutingEventType.stt_or_translation_latency_high.ToString(), "{}", ct);
            }
            else if (isSttDegraded && newSttEma < SttRecoveryMs)
            {
                isSttDegraded = false;
                updates.Add(new HashEntry("is_stt_degraded", false));
                _ = _eventProcessor.ProcessEventAsync(payload.RoomId, null, AudioRoutingEventType.pipeline_recovered.ToString(), "{}", ct);
            }
        }
        else if (payload.WorkerType == "tts")
        {
            double newTtsEma = oldTtsEma == 0 ? payload.LatencyMs : (payload.LatencyMs * alpha) + (oldTtsEma * (1 - alpha));
            updates.Add(new HashEntry("tts_ema", newTtsEma));

            if (!isTtsDegraded && newTtsEma > TtsDegradedMs)
            {
                isTtsDegraded = true;
                updates.Add(new HashEntry("is_tts_degraded", true));
                _ = _eventProcessor.ProcessEventAsync(payload.RoomId, null, AudioRoutingEventType.tts_latency_high.ToString(), "{}", ct);
            }
            else if (isTtsDegraded && newTtsEma < TtsRecoveryMs)
            {
                isTtsDegraded = false;
                updates.Add(new HashEntry("is_tts_degraded", false));
                _ = _eventProcessor.ProcessEventAsync(payload.RoomId, null, AudioRoutingEventType.voice_recovered.ToString(), "{}", ct);
            }
        }

        // 5. Lưu trạng thái mới về Redis Hash
        await db.HashSetAsync(hashKey, updates.ToArray());
    }
}
```

---

#### 3.3 Các rủi ro hệ thống và Cách khắc phục (Limitations & Mitigations)

1. **Clock Drift (Lệch đồng hồ server):** Tránh việc sai lệch phép tính End-to-End do clock drift bằng cách cấu hình đồng bộ tất cả Node vật lý trong cụm máy chủ qua chung một dịch vụ NTP Server.
2. **VAD Silence (Đóng băng EMA khi im lặng):** Nếu người dùng không nói trong thời gian dài, AI Worker không gửi Telemetry $\Rightarrow$ EMA bị đóng băng ở mức cao.
   - *Mitigation:* Thêm cơ chế **Time-decay** trong C#. Nếu quá 5 giây không nhận được telemetry nào từ phòng họp, tự động khấu trừ (decay) EMA của phòng đó về mức an sau (`SttEma = 1000ms`, `TtsEma = 2000ms`).
3. **Cold Start Penalty (Khởi động lạnh mô hình):** Chunk đầu tiên nạp mô hình vào VRAM mất 3-4 giây $\Rightarrow$ EMA lập tức báo động đỏ giả.
   - *Mitigation:* Áp dụng **Warm-up Period** bằng cách bỏ qua không tính toán EMA đối với 3 audio chunks đầu tiên của mỗi phòng họp mới mở.

---

#### 3.4 Tất cả Workers — Subscribe và Adapt theo Route Status

Khi nhận được thông báo Pub/Sub `translationRoom:{roomId}:route_updated`, các AI Workers tự điều chỉnh chế độ xử lý để tối ưu tài nguyên (VRAM/CPU):
```python
async def _on_route_status_changed(self, room_id: str, new_status: str):
    match new_status:
        case "PAUSED":
            self._paused_rooms.add(room_id)  # Tạm ngừng xử lý audio chunk của room này
        case "AUDIO_ROUTING_ACTIVE":
            self._paused_rooms.discard(room_id)
        case "VOICE_OUTPUT_DEGRADED":
            # TTS Worker: XTTS sập hoặc chậm, chủ động fallback dùng Edge-TTS mặc định
            if self.worker_name == "tts":
                self.use_edge_tts_fallback(room_id, True)
        case "TEXT_ONLY_MODE":
            # TTS Worker: Dừng hẳn model sinh giọng nói để tiết kiệm điện/VRAM GPU
            if self.worker_name == "tts":
                self.shutdown_tts_for_room(room_id)
        case "STOPPING" | "COMPLETED":
            # Tất cả các Workers giải phóng hoàn toàn bộ đệm và state của phòng
            self._cleanup_room(room_id)
```

---

### PHASE 4 — Gateway: Thông báo Status tới Client

#### 4.1 `NotificationRedisSubscriberService`
Subscribe kênh `translationRoom:{roomId}:route_updated` và forward qua SignalR:

```csharp
// Khi nhận được route update event
await _hubContext.Clients
    .Group($"translationRoom:{roomId}")
    .SendAsync("AudioRouteStatusChanged", new {
        routeId = routeId,
        oldStatus = oldStatus,
        newStatus = newStatus,
        timestamp = DateTime.UtcNow
    });
```

**Mapping từ Status → UI Message:**
| Status | Message hiển thị trên Client |
|--------|------------------------------|
| `TRANSLATION_DEGRADED` | ⚠️ "Đường truyền chậm, chất lượng dịch có thể giảm" |
| `VOICE_OUTPUT_DEGRADED` | ⚠️ "Giọng nói bị ảnh hưởng, đang cố gắng khôi phục" |
| `TEXT_ONLY_MODE` | ⚠️ "Chỉ còn chế độ văn bản, âm thanh không khả dụng" |
| `PAUSED` | ⏸️ "Phiên dịch đã tạm dừng" |
| `AUDIO_ROUTING_ACTIVE` | ✅ "Đường truyền đã ổn định" |
| `STOPPING` | 🔄 "Đang kết thúc phiên..." |
| `FINALIZING_ARTIFACTS` | 🔄 "Đang lưu transcript và bản ghi..." |
| `COMPLETED` | ✅ "Phiên đã kết thúc" |

---

### PHASE 5 — Thêm PAUSED State vào State Machine Backend

#### 5.1 Bổ sung vào `AudioRouteStateMachine.cs`
```csharp
// Priority override: Pause từ bất kỳ active state nào
if (eventType == AudioRoutingEventType.host_pauses_session)
{
    var pauseableStates = new[] {
        AudioRouteStatus.AUDIO_ROUTING_ACTIVE,
        AudioRouteStatus.TRANSLATION_DEGRADED,
        AudioRouteStatus.VOICE_OUTPUT_DEGRADED,
        AudioRouteStatus.TEXT_ONLY_MODE,
    };
    if (pauseableStates.Contains(currentState))
        return Result.Success(AudioRouteStatus.PAUSED);
}

// Trong switch expression:
AudioRouteStatus.PAUSED => eventType switch
{
    AudioRoutingEventType.host_resumes_session => Result.Success(AudioRouteStatus.AUDIO_ROUTING_ACTIVE),
    _ => InvalidTransition(currentState, eventType)
},
```

---

### PHASE 6 — Artifacts Finalization

#### 6.1 Quy trình kết thúc phòng chi tiết

```
EndRoom API Call
     │
     ▼
Room.Status = ENDED
     │
     ▼
Trigger: host_ends_session → AudioRoute.Status = STOPPING
     │
     ▼ (Auto-trigger ngay sau)
Background Job: ArtifactsFinalizationJob
     │
     ├── Đợi STT buffer flush (30s timeout)
     ├── Trigger: stop_routing_and_flush_data → FINALIZING_ARTIFACTS
     │
     ├── [Parallel Jobs]
     │    ├── Link Transcript records → TranscriptService gRPC
     │    ├── Upload recording file → Storage Service
     │    └── Request AI Summary → ai_assistant_worker
     │
     └── Khi tất cả xong:
          └── Trigger: transcript_recording_summary_linked → COMPLETED
```

#### 6.2 Files cần tạo mới
- `translation-room/src/.../Services/ArtifactsFinalizationService.cs` — Xử lý logic job
- `translation-room/src/.../HostedServices/ArtifactsFinalizationBackgroundService.cs` — Hosted Service lắng nghe STOPPING event

#### 6.3 Đặc tả cơ chế kiểm tra các điều kiện hệ thống đặc thù

##### A. Kiểm tra Graceful Flush (Event-Driven kết hợp 30s Timeout Fallback) sau khi Host dừng phòng (`host_ends_session` -> `STOPPING` -> `FINALIZING_ARTIFACTS`)
Khi Host bấm nút **Kết thúc** phòng họp, `TranslationRoomService` cập nhật trạng thái phòng họp thành `ENDED` và gửi sự kiện `host_ends_session`. Trạng thái Audio Route chuyển sang `STOPPING`.

Để tối ưu hóa hiệu năng và loại bỏ tải Polling vô ích lên Redis, hệ thống kết hợp **luồng Event-Driven chủ động** với **cơ chế Timeout cứu hộ 30 giây**:

1. **API Gateway gắn cờ Kết thúc:** Chặn không cho client push thêm chunk mới, đồng thời gắn cờ `is_final_chunk = true` vào chunk âm thanh cuối cùng được nhận tại stream `audio:chunks:{roomId}`.
2. **AI Workers forward tín hiệu hoàn tất:** STT Worker và TTS Worker khi hoàn tất xử lý chunk mang cờ `is_final_chunk` này sẽ publish một sự kiện đặc thù mang tên `final_chunk_processed` lên stream sự kiện hệ thống `translationRoom:system_events`.
3. **Hosted Service xử lý Song song:** 
   `ArtifactsFinalizationBackgroundService` khi chuyển Route sang `STOPPING` sẽ:
   - **Tuyến chủ động (Event-Driven):** Đăng ký lắng nghe sự kiện `final_chunk_processed` của phòng họp từ Redis. Khi nhận đủ tín hiệu hoàn tất từ TTS/STT, lập tức phát lệnh `stop_routing_and_flush_data`.
   - **Tuyến cứu hộ (Timeout Guard):** Khởi tạo một Timer đếm ngược 30 giây. Nếu sau 30 giây (vì lý do AI Worker crash, network drop giữa chừng) không nhận đủ sự kiện `final_chunk_processed`, Timer tự động phát tín hiệu `stop_routing_and_flush_data` để cứu nguy, đảm bảo phòng họp không bao giờ bị nghẽn trạng thái vĩnh viễn.

```csharp
public class ArtifactsFinalizationBackgroundService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IAudioRouteEventProcessorService _eventProcessor;
    private readonly ILogger<ArtifactsFinalizationBackgroundService> _logger;

    public async Task ExecuteGracefulFlushAsync(Guid roomId, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        
        // 1. Tuyến chủ động: Đăng ký nhận sự kiện hoàn tất từ Redis Pub/Sub
        var subscriber = _redis.GetSubscriber();
        string channel = $"translationRoom:{roomId}:final_processed";
        
        await subscriber.SubscribeAsync(channel, (chan, msg) => {
            _logger.LogInformation("Received event-driven completion signal for room {RoomId}", roomId);
            tcs.TrySetResult(true);
        });

        // 2. Tuyến cứu hộ: Hẹn giờ 30 giây
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            // Chờ sự kiện hoặc hủy bỏ khi hết 30s timeout
            await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Event-driven flush timed out for room {RoomId}. Fallback to emergency flush.", roomId);
        }
        finally
        {
            await subscriber.UnsubscribeAsync(channel);
            
            // 3. Chuyển tiếp sang FINALIZING_ARTIFACTS
            await _eventProcessor.ProcessEventAsync(
                roomId, 
                null, 
                AudioRoutingEventType.stop_routing_and_flush_data.ToString(), 
                "{}", 
                ct
            );
        }
    }
}
```

##### B. Kiểm tra hoàn thành lưu trữ Transcript & AI Summary vào DB (`FINALIZING_ARTIFACTS` -> `COMPLETED`)
Khi Audio Route ở trạng thái `FINALIZING_ARTIFACTS`, backend thực thi tác vụ kết xuất và lưu trữ tài nguyên cuộc họp. Để tối ưu hiệu năng, backend chạy song song các tác vụ thông qua cơ chế bất đồng bộ của C#:

```csharp
public async Task FinalizeRoomArtifactsAsync(Guid roomId, CancellationToken ct)
{
    try
    {
        // Thực thi song song 3 tác vụ nặng ký cuối phòng
        var saveTranscriptTask = _transcriptService.SaveFinalTranscriptsToDbAsync(roomId, ct);
        var generateSummaryTask = _summaryService.GenerateAndSaveMeetingSummaryAsync(roomId, ct);
        var saveRecordingTask = _recordingService.UploadAndLinkRecordingAsync(roomId, ct);

        // Chờ tất cả hoàn tất thành công
        await Task.WhenAll(saveTranscriptTask, generateSummaryTask, saveRecordingTask);

        // Nếu tất cả tác vụ hoàn tất không gặp lỗi, kích hoạt sự kiện terminal đóng băng phòng
        await _eventProcessor.ProcessEventAsync(
            roomId, 
            null, 
            AudioRoutingEventType.transcript_recording_summary_linked.ToString(), 
            "{}", 
            ct
        );
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Lỗi xảy ra khi lưu trữ transcript/summary cuối phòng {RoomId}", roomId);
        // Giữ nguyên trạng thái FINALIZING_ARTIFACTS để đảm bảo không mất mát dữ liệu và cho phép retry
    }
}
```

##### C. Kiểm tra cấu hình Ngôn ngữ/Vai trò trước khi chạy phòng (`IDLE` -> `ROUTING_READY`)
Routing âm thanh không thể hoạt động nếu thiếu thông tin cấu hình ngôn ngữ nguồn, đích hoặc thiếu các vai trò kết nối phù hợp (Speaker/Listener).

Khi Host lưu thiết lập ngôn ngữ qua API (`UpdateTranslationRoomSettingsAsync`) hoặc khi một người tham gia cập nhật ngôn ngữ nói/nghe thành công:
1. **Kiểm tra Cấu hình Phòng:**
   ```csharp
   bool isRoomLanguagesConfigured = !string.IsNullOrEmpty(room.SourceLanguage) && 
                                    !string.IsNullOrEmpty(room.TargetLanguages);
   ```
2. **Kiểm tra Vai trò thông qua LanguagePolicy:** Xác định xem trong phòng họp đã có ít nhất một cặp dịch hợp lệ (ví dụ: có thành viên nói tiếng Việt - Source Language và thành viên nghe tiếng Anh - Target Language) hay chưa:
   ```csharp
   bool hasActiveTranslationFlow = await _languagePolicy.HasValidRoutingParticipantsAsync(room.Id, ct);
   ```
3. **Trigger Event:** Nếu cấu hình hoàn chỉnh và có luồng dịch khả dụng, Backend kích hoạt sự kiện sẵn sàng:
   ```csharp
   await _audioRouteEventProcessor.ProcessEventAsync(room.Id, null, AudioRoutingEventType.participants_and_languages_configured.ToString(), "{}", ct);
   ```
   Trạng thái chuyển từ `IDLE` sang `ROUTING_READY`.

---

## 5. Thứ tự triển khai đề xuất (Priority Order)

| Task | Priority | Ước tính | Phụ thuộc |
|------|----------|----------|-----------|
| Thêm PAUSED transitions vào StateMachine | P0 | 2h | DONE: enums đã có |
| Trigger Pause/Resume trong TranslationRoomService | P0 | 1h | P0 trên |
| STT Worker: latency monitoring + publish events | P1 | 4h | — |
| TTS Worker: unavailable detection + fallback | P1 | 4h | — |
| Translation Worker: latency monitoring | P1 | 3h | — |
| All Workers: subscribe route_updated + adapt | P1 | 6h | P1 trên |
| Gateway: forward AudioRouteStatusChanged → Client | P1 | 3h | Backend Pub/Sub |
| ArtifactsFinalizationService | P2 | 8h | TranscriptService |
| Dead Letter Queue handling | P2 | 3h | Consumer Service |
| Consumer name dynamic (scaling) | P2 | 1h | — |

---

## 6. Redis Key Schema (Chuẩn chung)

| Key | Type | Producer | Consumer |
|-----|------|----------|---------|
| `audio:chunks:{roomId}` | Stream | Client (Gateway) | stt_worker |
| `stt:results:{roomId}` | Stream | stt_worker | translation_worker, Gateway, TranscriptService |
| `translate:results:{roomId}` | Stream | translation_worker | tts_worker, Gateway, TranscriptService |
| `tts:results:{roomId}` | Stream | tts_worker | Gateway |
| `ai_assistant:results:{roomId}` | Stream | ai_assistant_worker | Gateway |
| `translationRoom:system_events` | Stream | AI Workers, Backend | TranslationRoomService (Backend) |
| `translationRoom:{roomId}:route_updated` | Pub/Sub | Backend (Processor) | Gateway, AI Workers |
| `translationRoom:{roomId}:routes` | Hash | Backend (Cache) | AI Workers (đọc để biết config) |

---

## 7. Rủi ro và Mitigation

| Rủi ro | Mức độ | Mitigation |
|--------|--------|-----------|
| Event bị mất khi Redis restart | Cao | Dùng Redis Streams (persistent) thay vì Pub/Sub thuần |
| AI Worker bị die, không ai phát hiện recovery | Trung | Backend chạy Health Check định kỳ 30s, tự trigger recovery nếu không thấy event |
| Race condition: nhiều event cùng lúc | Trung | `AudioRouteStateMachine` là deterministic, chỉ có 1 thread xử lý per route |
| VRAM hết khi TTS crash | Cao | Fallback về `edge-tts` (Cloud TTS) khi XTTS không khả dụng |
| Billing sai khi crash ở `FINALIZING_ARTIFACTS` | Cao | Idempotent job ID — kiểm tra trạng thái trước khi tính tiền |

---

## 8. Hạn chế, Thách thức & Tech Debt định hướng tương lai (Future Optimizations & Tech Debt)

Nhờ áp dụng thiết kế thông minh ngay từ khâu đặc tả sơ bộ, các hạn chế lớn về **Telemetry phân tán (8.1)**, **EMA Alpha thích ứng động (8.3)**, và **Event-Driven Graceful Flush (8.4)** đã được tích hợp trực tiếp thành giải pháp kiến trúc cốt lõi của hệ thống.

Khoản Tech Debt còn lại cần theo dõi tiếp trong tương lai bao gồm:

### 8.1 Nguy cơ Rate Limit của Cloud Edge-TTS khi Fallback hàng loạt
*   **Vấn đề:** Khi cụm XTTS gặp sự cố (CUDA OOM hoặc Model crash), toàn bộ phòng họp sẽ đồng loạt kích hoạt cơ chế fallback chuyển hướng qua Edge-TTS Cloud API. Điều này có thể dẫn đến việc IP của máy chủ bị Microsoft Edge-TTS chặn (Rate Limit) do gửi quá nhiều yêu cầu đồng thời.
*   **Giải pháp tương lai:**
    *   Xây dựng một **Local TTS Cache Proxy** tại Backend để lưu trữ các câu nói/từ vựng phổ biến (như các câu chào, phản hồi ngắn gọn) tránh tổng hợp lại.
    *   Cài đặt một cụm mô hình TTS mã nguồn mở siêu nhẹ (ví dụ: Piper TTS hoặc Coqui-TTS Fast-Inference) chạy bằng CPU/GPU dự phòng ngay trong mạng nội bộ cluster để làm tầng đệm trước khi gọi tới Cloud API.

