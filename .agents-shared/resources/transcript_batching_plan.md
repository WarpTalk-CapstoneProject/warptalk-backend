# Triển khai Write-Behind Batching cho Transcript Service

Bản thiết kế này giải quyết triệt để bài toán lưu trữ tốc độ cao (High-throughput persistence) cho tính năng Transcript mà không làm rớt dữ liệu, không sập Database, và cũng không bắt Frontend phải "gánh" logic bằng LocalStorage.

## User Review Required

Bạn vui lòng review qua thiết kế kỹ thuật (System Design) bên dưới. Trọng tâm của giải pháp là việc "Chắp vá dữ liệu ở Read-time" (Backend tự xử lý Eventual Consistency). Nếu bạn đồng ý với hướng đi này, tôi sẽ tiến hành cập nhật source code cho `TranscriptService`.

---

## Proposed Architecture

### 1. Read-Time Merging (Giải quyết độ trễ mà không cần LocalStorage)
Để Frontend giữ nguyên tính chất "Dumb UI" (chỉ việc gọi API và render), khi Frontend gọi API lấy lịch sử phòng họp:
- Backend sẽ query `PostgreSQL` để lấy danh sách transcript đã được lưu cứng.
- Cùng lúc đó, Backend dùng `Redis` quét trực tiếp vào Stream `stt:results:{roomId}` để kéo ra những câu transcript mới nhất (những câu đang chờ trong hàng đợi 3 giây, chưa kịp vào DB).
- Backend **Merge (Trộn)** 2 tập dữ liệu này lại, loại bỏ các dòng bị trùng lặp dựa theo `SegmentId`, sau đó sắp xếp theo `StartTimeMs` và trả về duy nhất 1 danh sách hoàn chỉnh cho Frontend.
- **Kết quả:** F5 không bao giờ bị khuyết dữ liệu.

### 2. TranscriptBackgroundWorker (Gom lô & Giới hạn RAM)
Tạo một `BackgroundService` chạy ngầm bên trong Microservice Transcript:
- Tham gia vào Redis Stream dưới dạng một Consumer Group mới (độc lập hoàn toàn với SignalR Gateway).
- Vòng lặp: Kéo data -> Đưa vào bộ đệm tạm trên RAM -> Đủ **3 giây** hoặc đủ **50 câu** thì sẽ tiến hành đóng gói.
- **OOM Prevention:** Khi Gateway hoặc AI đẩy dữ liệu vào Stream, lệnh `XADD` sẽ đính kèm tham số `MAXLEN ~ 10000`. Redis sẽ tự động cắt bỏ phần đuôi cũ nhất để đảm bảo RAM không bị lấp đầy.

### 3. Database Idempotency (Chống lưu trùng lặp dữ liệu rác)
- Triển khai logic `BulkUpsert` tại Database Layer của `TranscriptService`. 
- Cấu hình Entity Framework Core hoặc dùng Raw SQL với cú pháp `ON CONFLICT (SegmentId) DO NOTHING`.
- **Validation Fallback:** Nếu một câu bị lỗi Format và không thể chèn vào DB, Worker sẽ bắt `try/catch`, ghi Log (hoặc đẩy vào Dead-letter-queue) và vẫn gọi `XACK` để bỏ qua tin nhắn đó, đảm bảo tiến trình Batching không bị kẹt vĩnh viễn (Poison message avoidance).
- Chỉ khi lệnh Upsert thành công, Worker mới gọi `XACK` (Acknowledge) về Redis để xóa an toàn danh sách vừa lưu.

### 4. Background Cleanup Job (Dọn rác Redis)
Để giải phóng tối đa bộ nhớ RAM cho cụm Redis, hệ thống sẽ có cơ chế dọn dẹp triệt để các Stream rác khi phòng họp đã kết thúc.
- Triển khai một `BackgroundService` chạy định kỳ (ví dụ mỗi giờ hoặc mỗi đêm).
- Job này sẽ quét Database để lấy danh sách các phòng họp có `Status = Ended`.
- Với mỗi phòng đã kết thúc, Job sẽ thực hiện lệnh `DEL` hoặc `UNLINK` để xóa hoàn toàn key Redis Stream (ví dụ: `stt:results:{roomId}`) ra khỏi bộ nhớ.
- Kết hợp với giới hạn `MAXLEN` ở bước 2, cơ chế này đảm bảo máy chủ Redis sẽ không bao giờ bị tràn RAM (OOM) dù hệ thống chạy liên tục nhiều năm.
