# Biên bản họp — phần logic còn thiếu

Ghi lại 2026-09-07, sau khi sửa lỗi `ReviseAsync` không bao giờ chạy được và bổ sung lựa chọn
template. Mỗi mục nêu **triệu chứng người dùng gặp**, chứ không phải "code chưa đẹp" — thứ tự ưu
tiên bên dưới dựa trên việc người dùng có mất dữ liệu hay hiểu sai bản ghi hay không.

Đã xong trong đợt này, để khỏi lẫn: index `(workspace_id, minutes_no, version)`; đánh số bỏ qua
bản đã supersede; `ReviseAsync` chạy hai statement trong một transaction; hai template `global-en`
(mặc định) và `vn-nd30`; `MinutesVote` có `movedBy`/`secondedBy`/`outcome`; tên hiển thị suy ra từ
`content.meetingTitle`; tìm kiếm đọc tên trong tài liệu thay vì tên phòng hiện tại.

---

## P1 — Người dùng mất quyền truy cập vào chính bản ghi của họ

### 1.1 Không tải được bản đã bị supersede

`ExportDocxAsync` gọi `GetCurrentAsync`, nên chỉ xuất được bản `is_current`. Sau một lần sửa đổi,
bản v1 **đã ký, đã thông qua** vẫn nằm trong DB, vẫn trả về qua API danh sách phiên bản, nhưng
không có đường nào lấy ra file.

Đây là mâu thuẫn với chính lý do module này tồn tại. Cả comment trong entity lẫn trong migration
đều nói bản cũ "stays readable afterwards" — hiện nó đọc được trên màn hình nhưng không cầm ra
được. Ai cần nộp bản đã ký cho kiểm toán thì không có cách nào.

**Cách làm:** thêm `GET /rooms/{roomId}/minutes/{minutesId}/export.docx`. Cổng quyền giữ nguyên
`RoomReadAccess` như bản hiện hành — tải là đọc. Tên file đã có sẵn hậu tố `-v{n}`.
Ước lượng: nhỏ. Chủ yếu là một overload của `ExportDocxAsync` nhận `minutesId` và kiểm tra
`minutes.TranslationRoomId == roomId` (cùng lý do `LoadForWriteAsync` kiểm tra).

### 1.2 Bấm Stop là mất bản ghi

Đã có ticket riêng — WT-644/WT-645, xem `recording-audit-wt644-wt645`. Nhắc ở đây vì nó chặn
đường "kiểm chứng lại một dòng trong biên bản": `atMs` in trên giấy chỉ có ích khi bản ghi còn.

---

## P2 — Bản ghi nói sai về chính nó

### 2.1 Publish không khoá được transcript

`artifactAccess` không được transcript service tôn trọng, xem
`transcript-service-ignores-artifact-access`. Hệ quả cho biên bản: dòng "chỉ host đọc được" trên
policy block sẽ là một bảo đảm không có gì đỡ lưng — đúng thứ mà quyết định branding đã cấm in.
**Không in dòng phân loại truy cập nào cho tới khi transcript service thật sự chặn.**

### 2.2 `BasedOnTranscriptVersion` luôn null

`CreateDraftAsync` đặt thẳng `null` kèm comment "cho tới khi có re-transcription". Nhưng cả
`MeetingMinutes` lẫn migration đều mô tả một cơ chế dựa vào nó: transcript chạy lại lần hai thì
biên bản đã duyệt phải **hiện ra là cần sửa đổi**, không bị viết đè.

Cơ chế đó hiện chưa tồn tại. Không có gì so sánh, nên không có gì cảnh báo. Một biên bản đã ký dẫn
một transcript đã bị thay có thể im lặng mâu thuẫn với nguồn của nó.

**Cách làm:** hai bước, bước một rẻ.
1. Ghi `BasedOnTranscriptVersion` lúc lập nháp (transcript service đã có `version`).
2. Khi đọc biên bản, so với version transcript hiện tại; lệch thì trả một cờ `needsRevision` để web
   hiện dải cảnh báo. Người đọc quyết định, hệ thống không tự sửa.

### 2.3 Số biên bản có thể tái sử dụng sau khi xoá

`CountForWorkspaceYearAsync` đếm để cấp số tiếp theo. Không có gì xoá biên bản hôm nay, nên chưa
thành lỗi — nhưng nếu sau này có đường xoá cứng, số sẽ được cấp lại cho tài liệu khác, và số biên
bản là tham chiếu ngoài. Một chuỗi tăng thật (`sequence`) hoặc cấm xoá cứng đều giải quyết được;
cấm xoá cứng đúng hơn với bản chất của loại tài liệu này.

---

## P3 — Người dùng phải làm thủ công thứ hệ thống biết

### 3.1 Không có template rỗng để tự điền

Câu hỏi ban đầu: user có tải được biểu mẫu trắng để tự điền không. **Chưa.** Chỉ tải được biên bản
đã có nội dung.

Thứ gần nhất hiện có là tác dụng phụ: trường nào không có giá trị thì in `…………………`, nên một biên
bản còn nhiều chỗ trống tải về gần giống một biểu mẫu.

**Cần chốt trước khi làm:** biểu mẫu trắng dùng để làm gì. Nếu là để họp không qua WarpTalk thì nó
không thuộc module này và nên là một file tĩnh tải từ trang trợ giúp. Nếu là để in ra ký tay thì
cái đang có đã đủ. Chỉ khi câu trả lời là "để điền rồi nạp ngược lên" thì mới thành việc thật, và
việc đó là một trình nhập liệu, không phải một nút tải.

### 3.2 Chưa có mặc định template ở cấp workspace

Hiện lựa chọn là **theo từng lần tải**, không lưu. Cố ý: lưu vào browser của một người sẽ khiến
cùng một tài liệu trông khác nhau với từng người trong đội.

Chỗ đúng để lưu là workspace settings — `MinutesTemplate` và `MinutesClassification` chính là nội
dung WT-643. Khi làm, `resolveMinutesTemplate({ stored })` ở web và `MinutesTemplates.Normalise` ở
backend đã chừa sẵn tham số cho nó.

### 3.3 Biểu quyết phải nhập tay hoàn toàn

`Votes` luôn rỗng khi lập nháp, và đúng như vậy — suy ra kết quả biểu quyết từ transcript là bịa.
Nhưng hệ thống cũng không có cách nào để **thu** biểu quyết: không có nút bấm trong phòng họp.

Nếu muốn phần biểu quyết có giá trị thật thì cần một cơ chế bỏ phiếu trong phòng, ghi ra
`MinutesVote` với `movedBy`/`secondedBy`/`outcome` do người điều hành chốt. Đây là tính năng mới,
không phải sửa lỗi.

---

## P4 — Vệ sinh kỹ thuật

### 4.1 Tìm kiếm theo tên không dùng được index

Mệnh đề tìm kiếm mới trích `content ->> 'meetingTitle'` rồi `lower()`. Đúng nhưng là seq scan, và
vì là khớp chuỗi con (`%term%`) nên btree thường không đỡ được. Khi thư viện đủ lớn, cách đúng là
**một index full-text phủ cả ba loại bản ghi** (biên bản, transcript, summary) — làm một lần thay
vì ba lần. Không nên vá riêng cho biên bản.

### 4.2 Hai template chưa được so trên cùng một tài liệu thật

`MinutesTemplateChoiceTests.Both_templates_carry_the_same_facts_about_the_meeting` kiểm bảy dữ
kiện. Nó bắt được lỗi bỏ sót cả khối, không bắt được lỗi bỏ sót một dòng trong khối. Một test so
sánh toàn bộ tập dữ kiện suy ra từ `MeetingMinutesContent` giữa hai layout sẽ chặt hơn.

### 4.3 `ReviseAsync` từng phụ thuộc thứ tự batch của EF

Đã sửa bằng transaction hai bước. Ghi lại vì bài học rộng hơn: **thứ tự UPDATE/INSERT trong một
`SaveChanges` không phải là đảm bảo.** Cùng một hình dạng, EF phát UPDATE trước ở `ReviseAsync` và
INSERT trước ở test seed của thư viện. Chỗ nào có partial unique index kiểu head-pointer thì phải
tách statement, đừng tin thứ tự.

---

## Không làm

- **Đổi `MeetingMinutes.Content` sang `JsonDocument`.** Sẽ mất đảm bảo round-trip nguyên văn —
  lý do cột đó là `string` là để một trường server chưa biết vẫn sống sót qua một lần lưu.
- **Thêm cột `meeting_title`.** Đã cân nhắc và loại: dưới DB, biên bản định danh bằng `minutes_no`;
  tên chỉ là thứ để hiển thị và được suy ra từ nội dung, nên không có bản sao thứ hai để lệch.
- **Bỏ template `vn-nd30`.** Với hồ sơ nộp trong nước nó mới là bản đúng.
