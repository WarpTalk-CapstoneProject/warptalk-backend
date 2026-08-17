# WarpTalk — Kịch bản demo bảo vệ (bản sân khấu)

Đây là bản để **diễn**: chỉ đường đi đúng, chỉ tính năng chạy được, mỗi flow kết bằng một
câu chốt giá trị. Không có bước nào cố tình cho hội đồng xem lỗi, màn hình *Access Denied*
hay tính năng còn thiếu.

Những ranh giới trung thực ("cái này chưa có", "cái kia mới là ý định") nằm ở
`DEMO-FLOWS.md` cùng thư mục — **đọc ở nhà, để trả lời khi bị hỏi**, không đọc trên sân khấu.

**Thứ tự:** Flow 1 → Flow 3 → Flow 2 → Flow 4 → Flow 5 (~30 phút).
Chuẩn bị giọng trước để Flow 2 dùng voice clone làm cao trào, và Flow 4 có dữ liệu thật
từ chính cuộc họp vừa diễn.

**Soát ngày 2026-08-13** trên cây `warptalk-web` nhánh `main`. Lần soát này kiểm lại:
điều hướng/sidebar, vị trí từng trang, dialog tạo phòng, màn hình join, nơi transcript &
summary sống, Member roles, Knowledge, đồng ý clone giọng. Các bước **bên trong phòng họp**,
**Billing** và **Admin** giữ nguyên theo `DEMO-FLOWS.md` (soát 2026-08-06).

> ⚠️ **Sản phẩm đã đổi so với kịch bản cũ — đừng diễn theo trí nhớ:**
> - **Không còn mục "Transcripts" trên sidebar.** Transcript, AI summary và file của một
>   cuộc họp nằm ngay trên trang cuộc họp đó: `/[slug]/rooms/[id]` → mục **Meeting record**.
> - **Không còn trang Invitations.** Lời mời và join request là các dòng trên **Members**.
> - Các trang đã có slug: `/[slug]/terminology`, `/[slug]/ai-chat`, `/[slug]/voice-profiles`.
> - Phòng họp live là `/[slug]/rooms/[id]/live` (link `/room/[id]` cũ tự chuyển hướng).
> - Có thêm **Knowledge** (`/[slug]/knowledge`) và **Member roles**
>   (`/[slug]/settings/member-roles`).
> - Lặp lại có **Daily / Weekly / Monthly**, và **Require approval** là công tắc riêng
>   trong menu ⋯ (không còn phụ thuộc loại cuộc họp).

---

## Chuẩn bị — làm hết trước khi vào phòng bảo vệ

| Việc | Vì sao |
|---|---|
| Tài khoản tạo workspace là **email doanh nghiệp** | Gmail/Outlook bị chặn ngay ở `/workspace/create` |
| Flow 1 chọn **subscription plan giới hạn 5 người** và seed sẵn **4 account/seat đã active** trong workspace demo | Để mời thêm 2 người: người thứ 5 vào được, người thứ 6 bị chặn bằng lỗi limit thật |
| Chuẩn bị **2 mailbox invitee** có thể mở được trong demo | Owner tạo invitation trong app, invitee phải accept từ email thật của họ |
| **Bật đồng ý clone giọng** cho cả 2 tài khoản tại `/[slug]/voice-profiles` | Không có bản ghi consent thì phòng họp **không clone**, cao trào Flow 2 mất |
| **Warm voice catalog** cho `vi` và `en` — chạy 1 lần dịch thật | Catalog là cache lười; rỗng thì voice picker không hiện |
| Sửa **fullName** của cả 2 tài khoản | Màn hình join không cho nhập tên; tile sẽ hiện nguyên email |
| Bật công tắc **"được tạo meeting"** cho tài khoản demo | Gate chặn ngay tại backend nếu tắt |
| **Host join qua UI trong app, khách join bằng meeting URL/link mời** | Flow 2 không demo nhập room code; meeting URL mới là đường vào chính |
| Flow 2 phải có workspace setting **Allowed languages = Vietnamese + English** trước khi tạo phòng | Để demo thử chọn Japanese/Korean/French bị chặn, chứng minh limit ngôn ngữ do Owner/Admin cấu hình được enforce |
| **Chạy thử 1 cuộc họp 2 máy trên chính production** | Đây là bước duy nhất phụ thuộc mạng và LiveKit |
| Tạo sẵn **1 voice profile** + chọn sẵn **1 giọng mặc định** | Ghi âm live tốn thời gian |
| Tạo sẵn **1 cuộc họp cũ đã có transcript + summary** | Flow 4 luôn có dữ liệu đẹp, không phụ thuộc Flow 2 |
| Tạo sẵn **1 workspace "rác"** | Để Flow 5 suspend, không đụng workspace đang demo |
| Đăng nhập Flow 4 bằng **tài khoản host** | Artifact mặc định HOST_ONLY |
| Mở sẵn tab: `/[slug]/history`, `/[slug]/payment/plans`, `/[slug]/terminology`, `/[slug]/ai-chat` | Mấy trang này không nằm trên sidebar |
| 2 máy + 2 tai nghe | Chống hú, và demo song ngữ bắt buộc 2 client |
| Video backup Flow 2 | Mạng hội trường là rủi ro số 1 |

---

## Flow 1 — Setup workspace: mua gói, tạo workspace, người, quyền, tri thức (~7 phút)

**Câu mở:** "WarpTalk không phải một công cụ dịch lẻ. Đơn vị của nó là workspace — có người,
có quyền, có thuật ngữ riêng, có chính sách AI, và tính phí được."

1. **Người dùng chưa đăng nhập vào landing page** — `/`. Kéo xuống phần **Subscription plans**,
   chọn gói phù hợp rồi bấm **Buy / Get started**. Hệ thống chuyển sang `/login` hoặc `/register`
   vì thao tác mua gói cần tài khoản.
2. **Đăng nhập / đăng ký** — `/login` hoặc `/register` → verify email nếu là tài khoản mới.
   Sau khi xác thực, Workspace Owner hoàn tất **thanh toán gói đã chọn**; thanh toán xong hệ thống
   chuyển về trang `/workspace` để bắt đầu tạo workspace.
3. **Tạo workspace** — `/workspace/create`: Name + Logo. Vào thẳng workspace mới.
4. **Giới thiệu điều hướng** — sidebar thật:
   - **Home · Meetings · Voice Profiles** (Meetings có sẵn danh sách phòng, meeting URL và *Create Meeting*)
   - **Members · Documents**, và Owner/Admin có thêm **Knowledge · Billing · Settings · Dashboard**
   - Tài khoản system admin có thêm nhóm **Platform** — để dành cho Flow 5.
5. **Mời thành viên + chứng minh giới hạn plan là thật** — `/[slug]/members` → **Invite**:
   email + role (Admin / Member). Owner tạo invitation, hệ thống gửi email mời; invitee mở mailbox
   của họ, bấm link trong email rồi **Accept invitation** để vào workspace.

   Edge case bắt buộc phải diễn: dùng plan giới hạn **5 người**, seed sẵn workspace đang có
   **4 seat active**. Mời 2 người liên tiếp:
   - Invitee A accept thành công và trở thành người thứ 5.
   - Invitee B accept hoặc được mời vượt quota thì hệ thống phải hiển thị lỗi rõ ràng kiểu
     **Plan member limit reached / Upgrade plan to invite more members**.

   Nói rõ: "Giới hạn trong subscription plan không phải chữ marketing; nó được backend enforce
   ngay trong luồng invitation/membership."
   Chỉ ra: danh sách này là **một chỗ duy nhất cho cả người đã vào và người đang trên đường vào** —
   mỗi dòng có trạng thái **Requested / Invited / Joined**, kèm chấm hiện diện online.
6. **Duyệt người xin vào** — trên chính trang Members: dòng **Requested** có **Approve**
   (chọn cho vào dạng *Internal* hay *External*) và **Reject**.
7. **Quyền** — vẫn trên Members: search, lọc, **công tắc "được tạo meeting"** cho từng người,
   **xuất danh sách ra Excel**. Nói: gate này **fail-closed** — tắt công tắc thì backend chặn,
   không phải chỉ ẩn nút.
8. **Đổi vai trò có kiểm soát** — `/[slug]/settings/member-roles` (Owner). Chọn một thành viên →
   hệ thống **hiện trước hệ quả** của việc thăng/giáng → **gõ đúng email để xác nhận** →
   áp dụng, và trả về một **biên nhận có mã audit**. Đây là điểm ăn điểm về quản trị:
   đổi quyền là hành động có xem trước, có xác nhận, có dấu vết.
9. **Settings workspace** — `/[slug]/settings`: ngôn ngữ mặc định, timezone, số phòng hoạt động
   tối đa, hạn lưu artifact, **danh sách ngôn ngữ được phép**, cộng tác với người ngoài, và
   **chính sách dùng AI**: cho phép LLM ngoài, **che PII**, **DLP + từ khoá cấm**, tông dịch,
   **kính ngữ tiếng Việt / keigo tiếng Nhật**.
   Đặt **Allowed languages = Vietnamese + English** cho buổi demo; giữ Japanese/Korean/French
   ở ngoài danh sách để Flow 2 chứng minh create-room bị chặn bởi policy của workspace.
10. **Thuật ngữ riêng** — `/[slug]/terminology`: tạo glossary → thêm term (Source, Target,
   Definition, Usage note). Nói trước: "lát nữa trong cuộc họp, thuật ngữ này sẽ xuất hiện
   đúng trong bản dịch."
11. **Documents** — `/[slug]/documents`: upload PDF/DOCX/XLSX/MD/ảnh, cờ **cho phép AI dùng**,
    quy trình duyệt, access policy theo người.
12. **Workspace Dashboard** — `/[slug]/dashboard`: quay về dashboard để chốt lại workspace đã được
    thiết lập xong: có gói đã thanh toán, có thành viên, có quyền, có settings, có thuật ngữ và
    có tài liệu làm nguồn tri thức cho AI.

    Nếu cần mở rộng thêm 30 giây, vào **Knowledge** — `/[slug]/knowledge`: chính là **những gì
    hệ thống đã học được** từ tài liệu và cuộc họp của workspace, mỗi dòng là một mẩu tri thức
    đọc được bằng mắt. Nói: "Trợ lý AI của WarpTalk không trả lời bằng trí tưởng tượng — đây là kho nó đọc."

**Câu chốt:** "Một workspace là một đơn vị vận hành có chính sách và có hoá đơn."

---

## Flow 3 — Giọng nói: giọng bạn nghe và giọng bạn cho mượn (~4 phút)

**Câu mở:** "Người dùng kiểm soát hai thứ: giọng mình *nghe*, và giọng mình *cho phép nhân bản*."

1. **`/[slug]/voice-profiles`**.
2. **Giọng có sẵn** — chọn ngôn ngữ → thư viện giọng của nhà cung cấp → bấm một giọng để đặt
   làm **mặc định cho ngôn ngữ đó**. Vào phòng họp tự áp dụng; chọn lại trong phòng thì lựa chọn
   trong phòng thắng.
3. **Tạo voice profile** — *Create profile*: tên, ngôn ngữ, rồi **ghi âm ngay trong trình duyệt
   hoặc upload file**, có câu mẫu hiển thị sẵn. Trình duyệt **tự chấm chất lượng mẫu** trước khi
   cho lưu (5–120 giây, ≤ 20 MB, một người nói, phòng yên) — nhấn mạnh: hệ thống không nhận mẫu rác.
4. **Đồng ý clone giọng** — thẻ **Voice cloning** ngay trên trang: một chỗ **duy nhất, tìm lại
   được**, ghi rõ thu gì, dùng làm gì, giữ bao lâu, rút lại lúc nào cũng được — và hệ thống
   **lưu lại đúng câu chữ đã hiển thị cùng thời điểm bấm**. Bật **Allowed**.
   Nói thẳng: "Giọng là dữ liệu sinh trắc. Đồng ý phải cho một lần, biết rõ, và rút lại được —
   không giấu trong thanh công cụ giữa cuộc họp."
5. **Danh sách profile** — mỗi hàng có trạng thái và nút **Delete**: người dùng xoá được dữ liệu
   giọng của chính mình.

**Câu chốt:** "Nhân bản giọng ở đây là một quyền được cấp, không phải một mặc định."

---

## Flow 2 — Cuộc họp thời gian thực (~10 phút, cao trào)

**Câu mở:** "Hai người nói hai thứ tiếng, vẫn hiểu nhau — và nghe bằng giọng của nhau."

1. **Tạo phòng** — nút **+** ở mục *Meetings*:
   - **Loại cuộc họp** ở đầu dialog: Event · Channel Meeting · Webinar · Company Meeting ·
     Virtual Appointment · Live Event. Loại quyết định cấu hình phòng ở server (số ghế,
     breakout…).
   - **Tập ngôn ngữ của phòng** — pill cờ: một phòng được định nghĩa bằng **tập ngôn ngữ sẽ được
     nói**, không phải "nguồn → đích". Mỗi người tự chọn ngôn ngữ nói/nghe của mình lúc vào.
     Ngôn ngữ nào bị chính sách workspace cấm thì hiện **Blocked** kèm lý do — nối thẳng về
     Settings đã xem ở Flow 1.

     Edge case bắt buộc phải diễn: vì Owner/Admin đã cấu hình workspace chỉ cho phép
     **Vietnamese + English**, hãy thử chọn thêm **Japanese/Korean/French** trước. Hệ thống phải
     chặn hoặc disable lựa chọn đó với lý do kiểu **Blocked by workspace language policy**.
     Sau khi hội đồng thấy limit là thật, bỏ ngôn ngữ bị chặn và chọn **Tiếng Việt + English**
     để tạo phòng demo chính.
   - **Menu ⋯** — mở ra và khoe 2 thứ:
     - **Repeat**: bật lên là hiện ngay giờ, chọn **daily / weekly / monthly**, weekly thì tick
       các thứ, monthly thì chọn ngày, cộng ô **Repeat until**. Pill *Daily 08:00* hiện ngay
       cạnh — trạng thái không bao giờ im lặng. Nói: mỗi buổi là **một phòng thật**, có meeting URL,
       transcript và hoá đơn riêng.
     - **Require approval**: bật → có phòng chờ. (Bật cái này cho buổi demo.)
   - **Mời theo email** ngay trong dialog.
   - **Create Room** → màn hình xác nhận có **meeting URL/link mời**, nút **Copy meeting URL**,
     **Configure** và **Join**. Không đọc room code, không nhập code trên sân khấu.
2. **Danh sách phòng cập nhật realtime** — máy 2 thấy phòng hiện ra trong UI app mà không cần F5.
   Invitee cũng có thể mở meeting URL từ email/link mời để vào đúng phòng.
3. **Màn hình kiểm tra thiết bị** — host bấm **Join** từ trang phòng trong app; khách mở
   **meeting URL** hoặc bấm phòng vừa hiện trong Meetings. Cả 2 máy chọn mic / camera / loa,
   **thanh đo mức mic**, preview hình, bật **noise suppression** và **background blur**,
   chọn **"tôi nói tiếng…" / "tôi nghe tiếng…"**, chọn **Voice + Text** hay **Text only** → Join.
   (Cho máy 2 vào bằng *Text only* nếu muốn khoe chế độ chỉ phụ đề.)
4. **Phòng chờ** — host thấy hàng chờ và bấm **Admit**; **Start meeting** đẩy mọi người vào.
   Nói: người đang chờ **không chiếm ghế** — ghế chỉ tính người đã kết nối.
5. **Trong phòng** — `/[slug]/rooms/[id]/live`: tile LiveKit + thanh điều khiển.
6. **Bật dịch** — **Start Translation** (bấm bằng đúng máy host). Side panel tự mở tab Transcript.
   > Nếu đã bấm **Start meeting** ở phòng chờ thì dịch đã chạy sẵn — cùng một endpoint.
7. **Nói thử** — A nói tiếng Việt, B nghe tiếng Anh. Mỗi bong bóng hiện **câu gốc + bản dịch +
   nhãn "Vietnamese → English"**, chạy realtime. Đổi chiều để đối chứng.
8. **Cao trào — giọng của chính người nói:**
   - Mở **voice picker** trên thanh điều khiển, đổi giọng cho ngôn ngữ đang nghe (có ghi rõ
     giới tính từng giọng) — nghe khác biệt ngay.
   - Bật **"dùng giọng clone của tôi"** → A nói tiếng Việt, **B nghe tiếng Anh bằng giọng của A**.
   - Chỉ rõ: quyền đã cấp ở Flow 3, còn đây là công tắc **của chính người nói, trong chính
     cuộc họp, tắt được bất cứ lúc nào**.
9. **Glossary có tác dụng** — nói một câu chứa term đã tạo ở Flow 1, chỉ vào bản dịch: đúng thuật
   ngữ công ty, không phải dịch máy chung chung.
10. **AI tự gợi ý trong transcript** — dải gợi ý một dòng nổi lên trên bong bóng, phân loại
    **Unanswered / Term / Action / Check / Reference**, tự biến mất sau 60 giây, không lưu lại.
    Nói: "Hệ thống nhận xét cuộc họp mà không ai phải hỏi nó."
11. **Chọn 2–3 tính năng phụ trợ thôi, đừng demo hết:**
    - **Chat đa ngữ**: dịch từng tin tại chỗ, gửi file, và **@WarpBot** ngay trong chat phòng họp.
    - **People**: spotlight, raise hand, tắt tuyến audio một người, **transfer host**.
    - **Host controls**: Lock room, Mute on entry, Mute all.
    - **Recording**: bấm **Record** khi **cả 2 máy đã ở trong phòng** — mọi người thấy chỉ báo
      đang ghi ngay lập tức.
    - **Breakout rooms**: chia nhóm, đặt thời lượng, hết giờ tự gom về phòng chính.
    - Reactions, đổi layout.
12. **Kết thúc** — **End meeting** → `/[slug]/rooms/[id]/ended`: trạng thái sinh artifact tự làm
    mới, kèm **Open artifacts · Submit feedback · View history**.

**Câu chốt:** "Rào cản ngôn ngữ biến mất mà danh tính giọng nói vẫn còn."

---

## Flow 4 — Sau cuộc họp: transcript, tóm tắt, hỏi đáp (~5 phút)

**Câu mở:** "Cuộc họp không bay đi mất. Nó tra lại được, tóm tắt được, và hỏi đáp được."

1. **Mở lại chính cuộc họp vừa họp** — **Meetings** → bấm vào cuộc họp đã kết thúc
   (`/[slug]/rooms/[id]`). Cuộn xuống dưới phần mô tả là mục **Meeting record**, 3 tab:
   - **Transcript** — từng câu có timestamp + người nói, gom theo phiên dịch. Host **sửa được
     từng câu** khi chưa chốt, và **Finalize** để khoá. Có **Copy** và **Download** (.txt).
   - **Summary** — **Overview / Decisions / Action items** (kèm người phụ trách), có **mẫu tóm tắt
     chọn được** và **trích dẫn về đúng mốc thời gian trong transcript**. Tự sinh sau khi họp,
     thường dưới 1 phút.
   - **Artifacts** — toàn bộ file gắn với cuộc họp: bản ghi, transcript, summary; tải về ngay đây.
2. **Hỏi đáp bằng WarpBot ngay tại trang này** — trợ lý nổi ở góc **biết nó đang đứng ở đâu**:
   gõ `/summarize`, `/action-items`, `/room-info` và nó hiện rõ đang làm gì
   ("Reading the transcript…", "Searching knowledge base…"). Hỏi thêm một câu tự nhiên bằng
   tiếng Việt về nội dung cuộc họp.
3. **Lịch sử toàn workspace** — `/[slug]/history`: bảng các phòng đã kết thúc
   (**Ended · Language route · Time · People · Outputs**), lọc **All / Completed / Cancelled /
   With outputs**, panel phải liệt kê **file còn giữ** kèm hạn lưu trữ theo chính sách workspace.
   Slash command của WarpBot cũng chạy ở trang này.
4. *(Tuỳ chọn)* `/[slug]/ai-chat` — trang hội thoại rời, có lịch sử.

**Câu chốt:** "Từ audio thô → transcript → tóm tắt → trả lời được câu hỏi. Đó là chuỗi giá trị
đầy đủ, không chỉ là một công cụ dịch."

---

## Flow 5 — Admin Portal (~3 phút)

**Câu mở:** "Trên tất cả các workspace còn một tầng vận hành."

1. **`/admin`** — metric toàn hệ thống.
2. **`/admin/workspaces`** — directory mọi workspace, filter/search, trạng thái
   **Active / Suspended / Deleted**; vào chi tiết `[workspaceId]` xem overview + tab audit.
3. **Suspend workspace "rác"** — **bắt buộc nhập lý do**, người thực hiện lấy từ token.
   Nói rõ ranh giới quyền: gate bằng **platform role**, hoàn toàn khác role Admin của workspace.
4. **Audit log append-only** — chỉ cho SELECT + INSERT: dấu vết quản trị không sửa được.
5. **`/admin/global-glossary`** — thuật ngữ dùng chung toàn hệ thống (publish / archive /
   bulk import), phân biệt với glossary riêng của workspace ở Flow 1.
6. **`/admin/billing`** — console billing nội bộ: usage, feature breakdown, top workspaces,
   subscriptions, invoices, alerts, service rates, điều chỉnh credit.

**Câu chốt:** "Đây là một nền tảng nhiều tổ chức, có vận hành và có kiểm toán — không phải một
ứng dụng đơn lẻ."

---

## Nếu hội đồng hỏi (trả lời ngắn, đừng mở thêm màn hình)

- **"Có hiện độ chính xác của bản dịch không?"** → Chưa, và đó là lựa chọn có chủ đích: chỉ số
  cũ đo *độ nghe rõ của STT*, không đo chất lượng dịch, nên nhóm đã đổi tên nó thay vì hiển thị
  một con số dễ hiểu nhầm. Chỉ số chất lượng dịch thật (back-translation / COMET) là hướng
  phát triển tiếp.
- **"Lặp lại theo tuần/tháng có sửa được sau khi tạo không?"** → Sửa được cả chuỗi; còn huỷ thì
  có cả hai mức: huỷ một buổi hoặc huỷ cả chuỗi.
- **"Có tự ghi hình theo loại cuộc họp không?"** → Bản ghi bắt đầu khi host bấm **Record**.
- **"Ai admit được người vào?"** → Host, và Owner/Admin của workspace.
- Mọi câu khác về "cái này có chưa": mục **"Những gì KHÔNG có"** cuối `DEMO-FLOWS.md`.
