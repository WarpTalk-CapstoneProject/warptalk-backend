# Centralized Telemetry & EMA Latency Tracking Plan

Tài liệu này mô tả phương án kiến trúc chuyển dịch logic tính toán độ trễ (Latency Math) từ Python Workers sang C# Backend, sử dụng thuật toán **Exponential Moving Average (EMA)** và **Hysteresis** để phát hiện độ trễ hệ thống một cách chính xác nhất.

---

## 1. Kiến trúc tổng quan (Architecture Overview)

**Mô hình phân chia trách nhiệm (Separation of Concerns):**
- **AI Workers (Python):** Hoạt động như các "cảm biến" (Sensors). Chỉ thực hiện nhiệm vụ đo đạc End-to-End Latency (thời gian từ lúc user gửi âm thanh đến lúc model trả kết quả) và gửi dữ liệu thô (Raw Telemetry) qua Redis. Không giữ State, không tự ra quyết định.
- **TranslationRoomService (C#):** Hoạt động như "bộ não" (Brain). Duy trì biến trạng thái EMA trên RAM (hoặc Redis) cho từng phòng. Dùng thuật toán EMA kết hợp Hysteresis để phân tích chuỗi dữ liệu thô, từ đó quyết định xem hệ thống có thực sự bị "Degraded" hay đã "Recovered" để chuyển đổi State Machine.

---

## 2. Luồng hoạt động chi tiết

### BƯỚC 1: Python Worker đo lường End-to-End Latency

Thay vì đo `time.monotonic()` trước và sau khi chạy Whisper, Worker sẽ so sánh **thời điểm hiện tại** với **thời điểm tạo ra audio chunk** để bao gồm cả thời gian chunk nằm chờ trong Queue (Queue Delay).

**Trong `stt_worker/worker.py`:**
```python
import time
import json

async def process(self, message_id, data):
    # 1. Thực thi model (Whisper)
    segments = await asyncio.to_thread(self.model.transcribe, audio_array)
    
    # 2. Tính toán End-to-End Latency
    # message_id của Redis stream có dạng "1681234567890-0" (Timestamp_ms - Sequence)
    # Hoặc lấy từ trường `timestamp` trong Payload do Gateway gửi.
    creation_timestamp_ms = int(message_id.decode('utf-8').split('-')[0])
    current_timestamp_ms = int(time.time() * 1000)
    e2e_latency_ms = current_timestamp_ms - creation_timestamp_ms
    
    # 3. Publish Raw Telemetry Event (KHÔNG publish State Machine Event)
    await self.redis_client.publish(
        "translationRoom:telemetry",
        json.dumps({
            "roomId": chunk.meeting_id,
            "workerType": "stt",
            "latencyMs": e2e_latency_ms,
            "timestamp": current_timestamp_ms
        })
    )
```

### BƯỚC 2: C# Backend tính toán Toán học (EMA + Hysteresis)

Backend sẽ có một `TelemetryProcessorService` (Singleton/MemoryCache) để cộng dồn các thông số gửi về.

**Thuật toán Exponential Moving Average (EMA):**
> $EMA_{new} = (Latency_{current} \times \alpha) + (EMA_{old} \times (1 - \alpha))$
- $\alpha$ (Alpha) là Smoothing Factor (Ví dụ: 0.3). Alpha càng lớn, hệ thống càng nhạy cảm với dữ liệu mới. Alpha càng nhỏ, EMA càng mượt.

**Ngưỡng Hysteresis (Chống Flap):**
- **Upper Bound (Ngưỡng suy thoái):** > 3000ms. Nếu EMA vượt mức này, C# Backend chủ động trigger event `stt_or_translation_latency_high` cho State Machine.
- **Lower Bound (Ngưỡng khôi phục):** < 1500ms. Phải giảm sâu xuống dưới mức này, Backend mới trigger event `pipeline_recovered`. Khoảng cách [1500ms - 3000ms] là vùng xám giữ nguyên trạng thái cũ.

**Trong `WarpTalk.TranslationRoomService.Application` (C#):**
```csharp
public class TelemetryProcessorService
{
    private readonly ConcurrentDictionary<Guid, RoomTelemetryState> _roomStates = new();
    private readonly IAudioRouteEventProcessorService _eventProcessor;
    
    private const double Alpha = 0.3; // Smoothing factor
    private const double DegradedThresholdMs = 3000.0;
    private const double RecoveryThresholdMs = 1500.0;

    public async Task ProcessTelemetryAsync(TelemetryPayload payload, CancellationToken ct)
    {
        var state = _roomStates.GetOrAdd(payload.RoomId, _ => new RoomTelemetryState());

        lock (state)
        {
            // Tính toán EMA
            if (state.EmaLatency == 0)
                state.EmaLatency = payload.LatencyMs; // Lần đầu tiên
            else
                state.EmaLatency = (payload.LatencyMs * Alpha) + (state.EmaLatency * (1 - Alpha));

            // Kiểm tra Hysteresis
            if (!state.IsDegraded && state.EmaLatency > DegradedThresholdMs)
            {
                state.IsDegraded = true;
                // Gọi State Machine
                _ = _eventProcessor.ProcessEventAsync(payload.RoomId, null, AudioRoutingEventType.stt_or_translation_latency_high.ToString(), "{}", ct);
            }
            else if (state.IsDegraded && state.EmaLatency < RecoveryThresholdMs)
            {
                state.IsDegraded = false;
                // Gọi State Machine
                _ = _eventProcessor.ProcessEventAsync(payload.RoomId, null, AudioRoutingEventType.pipeline_recovered.ToString(), "{}", ct);
            }
        }
    }
}
```

---

## 3. Lợi ích so với phương pháp Leaky Bucket tại Worker

1. **Chống "Flap State" tuyệt đối:** Dải Hysteresis (1500ms - 3000ms) đảm bảo rằng nếu mạng dao động quanh ngưỡng 2000ms, UI của người dùng sẽ không chớp tắt cảnh báo liên tục.
2. **Đo đạc End-to-End chính xác:** Bằng cách dùng Timestamp Redis trừ thời điểm xử lý xong, ta đo được thời gian thực tế mà người dùng phải chờ (Inference Time + Queue Delay Time).
3. **Mở rộng (Scalable) Worker dễ dàng:** Bạn có thể cắm 5 con `stt_worker` vào cùng 1 room. Mỗi con xử lý 1 chunk và gửi Telemetry về Backend. Backend sẽ cộng dồn EMA của cả 5 con một cách đồng nhất, không bị mất/trùng bộ đếm như khi lưu in-memory ở Python.
4. **Clean Architecture:** Python Worker hoàn toàn "stateless" (không có bộ nhớ trạng thái). Logic quyết định thay đổi trạng thái của phòng họp (Domain Logic) nằm trọn vẹn ở Backend C#, dễ debug và viết Unit Test cho thuật toán toán học.

---

## 4. Rủi ro cần lưu ý

1. **Thất thoát dữ liệu RAM (In-Memory Loss):** Dùng `ConcurrentDictionary` ở Backend nghĩa là nếu API Server bị restart, chỉ số EMA sẽ bị reset. 
   - *Mitigation:* Bắt đầu lại EMA từ đầu cũng không gây lỗi nghiêm trọng, sau vài giây (vài chunk) EMA sẽ nhanh chóng phản ánh lại độ trễ thực tế. Hoặc có thể lưu trạng thái EMA vào Redis Hash `translationRoom:{id}:ema`.
2. **Quá tải Redis Pub/Sub:** Gửi 1 message Telemetry cho MỖI chunk có thể tạo áp lực cho Redis.
   - *Mitigation:* Redis Pub/Sub cực kỳ nhanh (hàng trăm ngàn msg/s). Lưu lượng audio chunk (1s/chunk) là rất nhỏ, hoàn toàn nằm trong khả năng xử lý an toàn. Mọi tín hiệu gửi qua Pub/Sub cũng không tốn dung lượng ổ cứng.

---

## 5. Các hạn chế và Nhược điểm (Limitations & Drawbacks)

Mặc dù kiến trúc EMA End-to-End giải quyết được nhiễu (Flap), nó vẫn tồn tại các giới hạn lý thuyết:

1. **Lệch đồng bộ thời gian (Clock Drift/NTP Sync):**
   - End-to-End latency được tính bằng cách lấy `current_timestamp` (tại Worker) trừ đi `creation_timestamp` (tại Gateway/Client). Nếu máy chủ Backend và máy chủ GPU chạy Python Worker lệch nhau dù chỉ vài trăm mili-giây do NTP Sync bị lỗi, kết quả Latency sẽ bị sai hoàn toàn (có thể ra số âm).
   - *Mitigation:* Bắt buộc tất cả các Node trong cluster phải đồng bộ thời gian chặt chẽ qua chung một NTP Server.

2. **Dữ liệu Telemetry bị đóng băng khi không ai nói (VAD Silence):**
   - Nếu AI sử dụng VAD (Voice Activity Detection) để lọc bỏ tiếng ồn và im lặng, các chunk tĩnh sẽ không được đẩy vào Whisper $\Rightarrow$ Worker không gửi Telemetry. Nếu hệ thống đang bị `DEGRADED`, sau đó mọi người im lặng 10 giây, Backend sẽ không nhận được dữ liệu Telemetry mới nào. EMA bị "đóng băng" ở mức cao và phòng vẫn bị báo là DEGRADED dù hệ thống thực tế đã rảnh rỗi.
   - *Mitigation:* C# Backend cần implement một cơ chế Time-decay: Nếu quá 5 giây không có Telemetry nào gửi về, tự động ép xung dần EMA về lại ngưỡng an toàn.

3. **Cú sốc khởi động lạnh (Cold Start Penalty):**
   - Chunk audio đầu tiên khi phòng họp mới bắt đầu thường mất nhiều thời gian hơn (3-5 giây) để Whisper/XTTS nạp vào VRAM hoặc khởi động session. EMA sẽ lập tức vọt lên mức cao và trigger cảnh báo "Đường truyền chậm" giả ngay từ câu nói đầu tiên.
   - *Mitigation:* Bỏ qua việc đánh giá State Machine cho 3 chunk đầu tiên của mỗi phòng (Warm-up period).
