# Live Subtitle Decoupling — Technical Proposal

**Ticket:** [WT-587](https://linear.app/fpt-sep490-su26/issue/WT-587) (Spike, timebox 2 ngày)
**Tiền nhiệm:** [WT-408](https://linear.app/fpt-sep490-su26/issue/WT-408) đã chốt Option A (CC = display-only)
và ghi rõ Option B — CC là consent gate thật — "cần backend work và một product call, cố ý không làm ở đây".
Tài liệu này là Option B đó.

**Source of truth khi đọc code** (`origin/development`, 2026-08-28):

| Repo | SHA |
| :--- | :--- |
| warptalk-backend | `8ab94ae23fb3936e46efdeb4d36c294cf1737f17` |
| warptalk-web | `b0195a0b56d19233026bdcb16b0e2a9b7ca6afcb` |
| warptalk-ai | `413919ed7b9fdb3506407b7291af41fd0d56afea` |

---

## 0. Kết luận trước: tiền đề của spike sai một nửa

Ticket mở đầu bằng:

> dải phụ đề trực tiếp (`live-subtitle-overlay.tsx`) bị phụ thuộc trực tiếp 100% vào danh sách
> `transcriptSegments` được lưu trữ (persist) trong Database và Zustand Store.

**Không đúng.** Caption lane đã ephemeral sẵn từ trước spike này. Nó đọc Redis qua SignalR và
không bao giờ chạm Postgres. Hai luồng — broadcast và persistence — đã tách rời ở tầng Redis
consumer group từ lâu; cái chưa tách là **quyết định có ghi hay không**, chứ không phải đường đi
của dữ liệu.

Hệ quả cho scope:

| Vấn đề ticket nêu | Trạng thái thật |
| :--- | :--- |
| Subtitle phụ thuộc DB | **Sai.** Lane đọc `state.transcriptSegments`, nguồn duy nhất là SignalR. |
| Bật CC bắt buộc phải lưu transcript | **Đúng, nhưng không phải vì lane.** Persistence là một consumer độc lập, chạy vô điều kiện, không liên quan CC. |
| Tốn token AI dịch cho phòng nháp | **Sai.** Translation đã gated sau Start Translation từ WT-373. Phòng không bấm Start không tốn token dịch nào. |
| Tốn DB I/O cho phòng nháp | **Đúng.** Đây là vấn đề thật và duy nhất còn lại. |

Vậy công việc thật của WT-587 **không phải refactor frontend**. Là thêm một cờ và một `if` ở đúng
một consumer. Lane không cần sửa một dòng nào.

---

## 1. Luồng dữ liệu thật hôm nay

`stt:results` là một Redis Stream **fan-out**: mỗi consumer group đọc trọn vẹn bản sao của mình,
độc lập hoàn toàn với các group khác. Đây là điểm mấu chốt mà ticket bỏ sót.

```
                 mic (LiveKit track)
                         │
                         ▼
        livekit_ingress_worker  ── vào phòng theo pubsub `meeting.track_published`,
                         │          KHÔNG chờ Start Translation
                         ▼
                  stt:frames ──► stt_worker
                                     │
                                     ▼
                          ╔══════════════════════╗
                          ║  Redis  stt:results  ║   (fan-out, 5 consumer group)
                          ╚══════════════════════╝
                                     │
   ┌──────────────┬──────────────────┼──────────────────┬────────────────────┐
   ▼              ▼                  ▼                  ▼                    ▼
gateway-      transcript-       translate-         assistant-          suggestion-
consumers     persistence       workers            workers             workers
   │              │                  │                  │                    │
SignalR      Postgres           gated:            summary /            hint chips
Transcript   transcript_        _translation_     action items
SegmentRcvd  segments           active_for()
   │              │                  │
   ▼              ▼                  ▼
CAPTION LANE   TRANSCRIPT       translate:results ──► tts-workers ──► giọng dub
(RAM only)     (bản ghi)              │
                                      ├──► gateway-consumers ──► TranslationTextReceived (RAM)
                                      └──► transcript-persistence ──► translation_contents (DB)
```

### 1.1 Bằng chứng theo file

**Lane không đọc DB.**
- `live-subtitle-overlay.tsx:49` —
  `useTranslationRoomStore((state) => state.transcriptSegments)`. Không có prop nhận segment từ ngoài,
  không có query.
- `persistent-meeting-session.tsx:1878` —
  `connection.on("TranscriptSegmentReceived", ...)` → `addTranscriptSegment`. Đây là nguồn duy nhất
  của `transcriptSegments`.
- `AiResultConsumerService.cs:343` —
  Gateway đọc thẳng `stt:results` rồi `SendAsync("TranscriptSegmentReceived", segment)`. Không có
  repository, không có DbContext trong đường này.

**Lệnh DB duy nhất trong phòng live** là catch-up cho *transcript panel*, không phải lane:
`useTranscriptByRoom(roomId)` / `useTranscriptSegments(...)` tại `persistent-meeting-session.tsx:747`,
tồn tại để người vào muộn thấy được 20 phút đã trôi qua. Xoá persistence sẽ làm hỏng **panel**, không
làm hỏng **lane**.

**STT đã chạy độc lập với Start Translation.**
`livekit_ingress_worker/worker.py:1939` ghi
nguyên văn lý do gate cũ bị gỡ:

> Transcription is NOT translation, and this gate used to conflate them. It discarded every chunk
> until the room reported IN_PROGRESS/AUDIO_ROUTING_ACTIVE, a state only reached once someone started
> translation.

Gate thật nằm ở `translation_worker/worker.py:429`:
`if not await self._translation_active_for(...)` → log `translation_skipped_not_started`.

**Chi phí AI trước Start Translation = 0 cho dịch, ≠ 0 cho STT.**
`billing_worker/worker.py:115` — WT-344: chỉ
TRANSLATION và TTS mới tính tiền; STT bị bỏ khỏi billable set theo quyết định của owner
("transcription is what the meeting gets for free"). Nên lập luận "tốn token AI dịch thuật" trong
ticket không đúng với code hiện tại.

### 1.2 Bảng consumer group trên `stt:results`

| Group | Chủ | Tác dụng phụ | Đã gated? |
| :--- | :--- | :--- | :--- |
| `gateway-consumers` | Gateway | SignalR broadcast (RAM) | không cần |
| `transcript-persistence` | TranscriptService | **INSERT Postgres + embedding index** | **chưa — đây là việc cần làm** |
| `translate-workers` | translation_worker | gọi LLM, tốn tiền | ✅ Start Translation |
| `assistant-workers` | ai_assistant_worker | summary / action items | ✅ theo `__MEETING_END__` |
| `suggestion-workers` | suggestion_worker | hint chips | ✅ theo `min_words` + policy |

`transcript-persistence` là group duy nhất chưa có cổng. Đó là toàn bộ delta của spike này.

---

## 2. Trả lời ba câu hỏi của ticket

### Q1 — Làm sao bật CC mà không ghi DB?

**Không cần làm gì ở đường CC.** CC đã không ghi DB. Câu hỏi đúng phải là: *làm sao để một phòng
không ghi DB, trong khi CC vẫn chạy?*

Trả lời: thêm một cờ ở cấp **phòng** (không phải cấp người xem) và đọc nó ở
`TranscriptRedisConsumerService.ProcessSttMessageAsync` — sớm nhất có thể, trước
`GetOrCreateTranscript`, rồi `return true` (ACK, không dead-letter).

Cấp phòng chứ không cấp người xem, vì transcript là **một bản ghi chung của cuộc họp**. Nếu A tắt
và B bật thì bản ghi vẫn tồn tại và vẫn chứa lời của A — cờ per-viewer sẽ là một lời hứa privacy mà
kiến trúc không giữ được, đúng loại khoảng cách mà WT-408 sinh ra để đóng lại.

### Q2 — Cơ chế upgrade từ ephemeral sang persistent khi bấm Start Translation / Record?

**Đề xuất: không upgrade.** Cờ được chốt lúc tạo phòng và chỉ host đổi được **trước khi** phòng vào
`IN_PROGRESS`. Lý do:

- Upgrade giữa chừng tạo ra một bản ghi **thủng đầu**: transcript bắt đầu từ phút thứ 12, không ai
  biết 12 phút đầu đã nói gì hay có tồn tại không. Một bản ghi im lặng về khoảng trống của chính nó
  còn tệ hơn không có bản ghi.
- Không thể vá bằng backfill: audio ephemeral không được lưu, và `stt:results` có TTL —
  cái đã trôi qua là đã mất thật.
- Downgrade giữa chừng còn tệ hơn: hàng đã ghi vào Postgres rồi, tắt cờ không xoá được chúng, nên UI
  sẽ nói "không lưu" trong khi DB đang giữ nửa cuộc họp.

Nếu product vẫn muốn cho đổi giữa chừng, phải chấp nhận và **hiển thị** sự thật đó: transcript mang
nhãn "bắt đầu ghi từ HH:mm", và thao tác tắt giữa chừng phải xoá cứng các hàng đã ghi của phiên này
chứ không chỉ ngừng ghi tiếp. Đó là một ticket riêng, không nằm trong timebox này.

`Record` (LiveKit egress) là chuyện độc lập và **không** nên gộp: nó ghi video, đi qua đường khác, và
đã có nút riêng. Gộp hai thứ vào một cờ là tái lập đúng sự nhập nhằng mà WT-408 vừa gỡ.

### Q3 — Cờ `SaveMeetingTranscript` thiết kế ở DTO và DB Schema như thế nào?

Hiện tại **chưa có gì**: grep toàn bộ `warptalk-backend`, `warptalk-web`, `warptalk-infrastructure`
cho `SaveTranscript|SaveMeetingTranscript|PersistToDb|transcript_enabled` → 0 kết quả. Entity
`TranslationRoom` chỉ có đúng một cột bool là `IsActive`.

Thiết kế đề xuất ở mục 3.

---

## 3. Thiết kế đề xuất

> **Đã implement 28/08 — mục 3.1 dưới đây KHÔNG còn đúng.**
>
> Đề xuất ban đầu là thêm cột `translation_rooms.save_transcript` + migration. Khi vào code thì
> thấy `translation_rooms.settings` (jsonb) đã là nơi ở của đúng loại cờ này — `requires_approval`
> nằm đó, có default an toàn resolve trong `TranslationRoomMapper.ReadSettings`, đã đi sẵn qua
> create/update/DTO, và `UpdateTranslationRoomSettingsAsync` đã từ chối mọi sửa đổi khi phòng rời
> `SCHEDULED`/`WAITING` — tức là luật của Q2 có sẵn, không phải xây.
>
> Nên bản thực thi dùng `TranslationRoomSettings.SaveTranscript` (mặc định `true`), **không có
> migration**. Cổng vẫn đúng chỗ mục 3.2 nói. Hai điểm bản đề xuất chưa lường:
>
> - Cổng phải đặt ở **cả** `ProcessTranslateMessageAsync`, không chỉ STT. Không có hàng segment
>   thì check "Verify Segment Exists" trả `false` → retry → dead-letter mọi câu dịch của phòng
>   ephemeral. Mục 5 có cảnh báo dead-letter nhưng chưa nối được với nguyên nhân này.
> - Field gRPC phải là `optional bool`. proto3 default một `bool` trần là `false`, mà `false` ở
>   đây nghĩa là "vứt bản ghi cuộc họp này" — một server cũ sẽ vô tình ra lệnh ngừng ghi toàn hệ
>   thống. Có presence thì "vắng mặt" chỉ còn nghĩa "server cũ", và reader trả lời bằng cách ghi.

### 3.1 Database

Migration mới trong `warptalk-backend/translation-room/database/migrations/`
(theo convention: file là source, bản copy trong `warptalk-infrastructure` mới là cái prod apply,
runner tự mở transaction nên **không** viết `BEGIN`/`COMMIT` trong file).

```sql
-- 20260828xxxxxx_room_transcript_retention.sql
ALTER TABLE translation_rooms
    ADD COLUMN save_transcript BOOLEAN NOT NULL DEFAULT TRUE;

COMMENT ON COLUMN translation_rooms.save_transcript IS
    'FALSE = ephemeral meeting: captions and live translation still run, nothing is written to '
    'transcript_segments. Decided at creation; the host may only change it before the room reaches '
    'IN_PROGRESS. DEFAULT TRUE so every room that existed before this column keeps its transcript.';
```

`DEFAULT TRUE` là bắt buộc: mặc định FALSE sẽ im lặng tắt transcript của toàn bộ phòng đang chạy
ngay khoảnh khắc migration apply.

### 3.2 Backend — cổng duy nhất

`TranscriptRedisConsumerService.ProcessSttMessageAsync`, ngay sau khi resolve được `roomId` và
trước khi mở scope/UnitOfWork:

```csharp
// WT-587: một phòng ephemeral vẫn phát caption và vẫn dịch — nó chỉ không để lại vết.
// Kiểm ở đây chứ không ở gateway: gateway broadcast RAM, nó không phải thứ cần cấm.
// ACK (return true) chứ không return false — một phòng cố ý không lưu không phải lỗi cần retry
// rồi dead-letter.
if (!await _roomRetentionPolicy.ShouldPersistAsync(roomId, cancellationToken))
{
    return true;
}
```

`ShouldPersistAsync` đọc `save_transcript` qua gRPC `TranslationRoomService`, cache theo
`roomId` với TTL ~4h — đúng khuôn `_workspacePolicyCache` đã có sẵn trong chính file này
(`WorkspacePolicyCacheDuration`), vì cờ không đổi giữa chừng (Q2).

Cùng cổng đó chặn luôn `PublishEmbeddingIndexRequestAsync` — không có hàng thì không có gì để index,
nên không cần `if` thứ hai.

Cần chặn ở **cả** `ProcessTranslateMessageAsync` (bản dịch cũng là nội dung cuộc họp) — nếu chỉ chặn
STT thì `translation_contents` vẫn tích luỹ và `segment_translation_links` sẽ trỏ vào segment không
tồn tại.

### 3.3 API / DTO

- `CreateTranslationRoomRequest` + `UpdateTranslationRoomSettingsRequest`: thêm `bool? SaveTranscript`.
- `TranslationRoomDto`: thêm `bool SaveTranscript`.
- `UpdateTranslationRoomSettingsAsync`
  (`TranslationRoomService.cs:2324`) từ chối đổi cờ khi `Status == IN_PROGRESS` → `VALIDATION_ERROR`,
  message nói rõ vì sao (bản ghi thủng đầu), không im lặng bỏ qua.
- Proto `TranslationRoomService`: thêm field vào response mà TranscriptService đang gọi, để 3.2 đọc
  được mà không phải thêm hop mới.

### 3.4 Web

- Dialog tạo phòng: một checkbox **"Lưu transcript cuộc họp"**, mặc định bật, kèm dòng phụ
  *"Tắt để họp không lưu vết — phụ đề và bản dịch vẫn chạy bình thường, nhưng cuộc họp sẽ không có
  transcript, summary hay biên bản."* Câu đó phải nói đủ cái mất, vì nó cũng tắt luôn Flow 4 của demo.
- Trong phòng: một chip cạnh tên phòng khi `saveTranscript === false`, để người vào sau biết mình
  đang ở phòng không lưu vết mà không phải mở settings.
- **Không** đụng `live-subtitle-overlay.tsx`.

### 3.5 Cái không làm

- Không đổi `livekit_ingress_worker` — nó phải tiếp tục chạy STT, đó là thứ nuôi caption lane.
- Không thêm cờ per-participant (lý do ở Q1).
- Không gộp với nút Record.
- Không đổi ngữ nghĩa nút CC lần nữa — WT-408 đã chốt và microcopy đã đúng.

---

## 4. Implementation plan

| # | Việc | Repo | Ước lượng |
| :-- | :--- | :--- | :--- |
| 1 | Migration `save_transcript` + copy sang infrastructure | backend, infrastructure | 0.5 |
| 2 | Entity + DbContext `HasColumnName` + proto field | backend | 0.5 |
| 3 | Cổng ở `ProcessSttMessageAsync` + `ProcessTranslateMessageAsync` + cache | backend | 1 |
| 4 | Create/Update DTO + validation "không đổi khi IN_PROGRESS" | backend | 0.5 |
| 5 | Checkbox tạo phòng + chip trong phòng | web | 0.5 |
| 6 | Test: phòng `save_transcript=false` → 0 hàng, caption vẫn có, translate vẫn có | backend | 1 |

Tổng ~4 ngày người, **vượt timebox 2 ngày của spike** — nên spike dừng ở tài liệu này và mục 4 tách
thành ticket implement riêng.

## 5. Rủi ro

- **Demo.** Phòng ephemeral không có transcript ⇒ không có summary ⇒ Flow 4 của `DEMO-FLOWS.md` chết.
  Phòng demo phải để mặc định bật, và checkbox không được nằm ở vị trí dễ bấm nhầm.
- **`transcript-persistence` là group chung cho 4 stream.** Sửa nhầm chỗ sẽ ảnh hưởng cả `tts:results`
  và `translate:backfill_results`. Cổng phải đặt trong từng `Process*MessageAsync`, tuyệt đối không
  đặt ở vòng lặp `ExecuteAsync`.
- **ACK vs dead-letter.** Nếu cổng `return false`, mọi message của phòng ephemeral sẽ retry rồi
  dead-letter, và dashboard dead-letter sẽ báo động cho một hành vi cố ý.
