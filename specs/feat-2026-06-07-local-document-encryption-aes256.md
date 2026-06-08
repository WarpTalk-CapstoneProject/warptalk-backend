# Spec: Local Document Encryption using AES-256 and HMAC-SHA512
Date: 2026-06-07
Status: draft

## 1. Problem Statement
Khi triển khai giải pháp lưu trữ tài liệu cục bộ (Local Storage Provider) cho các doanh nghiệp, hệ thống WarpTalk không thể tận dụng cơ chế mã hóa tự động ở Rest (SSE) của các nhà cung cấp đám mây (như AWS S3 hay MinIO SSE). 

Để đảm bảo tính bảo mật dữ liệu, tránh việc quản trị viên máy chủ hoặc các tiến trình trái phép đọc trực tiếp các tệp tin lưu tại thư mục local, hệ thống cần triển khai cơ chế mã hóa dữ liệu ở mức ứng dụng (Application-Level Encryption) sử dụng thuật toán mã hóa đối xứng **AES-256** kết hợp xác thực toàn vẹn bằng **HMAC-SHA512** (mô hình Encrypt-then-MAC).

---

## 2. Bounded Context & Scoping Decisions

### 2.1. Phạm vi tác động
1. **Phạm vi áp dụng:** Chỉ áp dụng khi cấu hình `StorageProvider` trong `WorkspaceDocument` có giá trị là `"local"`. Các tài liệu lưu ở S3/MinIO sẽ tiếp tục sử dụng cơ chế mã hóa SSE của hạ tầng đám mây.
2. **Cấp khóa mã hóa (Key Derivation):** Sử dụng mã khóa riêng cho từng Workspace (`WorkspaceKey`). Khóa này được dẫn xuất từ Master Key cấu hình hệ thống kết hợp với `WorkspaceId` thông qua hàm băm một chiều bảo mật.

### 2.2. Lựa chọn và Chứng minh Giải thuật (Rationale & Proofs)
Quyết định lựa chọn **AES-256-CBC** kết hợp với **HMAC-SHA512** theo mô hình **Encrypt-then-MAC (EtM)** dựa trên các cơ sở kỹ thuật sau:
1. **AES-256 (độ an toàn cấp độ quân sự):** 
   * Độ dài khóa 256 bits chống lại mọi hình thức tấn công dò khóa (Brute-force) kể cả trước khả năng tính toán của máy tính lượng tử trong tương lai.
   * Hầu hết các CPU hiện đại đều hỗ trợ tập chỉ thị phần cứng **AES-NI**, giúp việc mã hóa đối xứng bằng C# đạt tốc độ cao (hàng GB/giây) với mức tiêu thụ CPU của máy chủ cực kỳ thấp.
   * Chế độ **CBC (Cipher Block Chaining)** đảm bảo tính ngẫu nhiên của các block dữ liệu kế tiếp nhờ kết hợp giá trị của block trước đó, tránh được việc rò rỉ mô thức dữ liệu (data patterns) giống như chế độ ECB.
2. **HMAC-SHA512 (Xác thực toàn vẹn và chống giả mạo):**
   * Mã hóa thông thường (kể cả CBC) chỉ bảo mật tính riêng tư của dữ liệu chứ không đảm bảo dữ liệu không bị thay đổi. Kẻ tấn công có thể thay đổi các bit trên bản mã (Bit-flipping attacks) để làm sai lệch kết quả giải mã.
   * **SHA-512** cung cấp độ dài mã băm 512-bit có khả năng chống trùng lặp (collision resistance) tuyệt đối, giúp phát hiện ngay lập tức bất kỳ sự thay đổi nhỏ nào của tệp tin.
   * **Chống tấn công Padding Oracle:** Việc kiểm tra chữ ký HMAC được thực hiện trước khi giải mã. Nếu chữ ký không khớp, luồng giải mã sẽ bị hủy ngay lập tức mà không chạy tiếp phần giải mã của AES, loại bỏ hoàn toàn nguy cơ bị khai thác lỗi rò rỉ padding từ phía kẻ tấn công.
3. **Mô hình Encrypt-then-MAC (EtM):**
   * Đây là mô hình tiêu chuẩn được khuyên dùng bởi các nhà mật mã học hiện đại (ứng dụng trong IPsec, TLS v1.3). Nó cho phép hệ thống kiểm tra chữ ký hợp lệ trước khi sử dụng tài nguyên CPU để giải mã, ngăn chặn các cuộc tấn công từ chối dịch vụ (DoS) bằng cách gửi các file bị sửa đổi để ép server giải mã liên tục.

---

## 3. Kiến trúc kỹ thuật và Quy trình mã hóa (AES-256 + HMAC-SHA512)

Cấu trúc định dạng tệp tin mã hóa lưu trữ dưới đĩa local bao gồm các phần:
```
+-----------------------------------+-----------------------------------+-----------------------------------+
|  Initialization Vector (IV)       |  Ciphertext                       |  HMAC-SHA512                      |
|  (16 bytes)                       |  (Kích thước biến đổi)            |  (64 bytes)                       |
+-----------------------------------+-----------------------------------+-----------------------------------+
```

### 3.1. Quy trình mã hóa (Encrypt Flow)
Khi người dùng tải lên tài liệu thông qua `UploadDocumentAsync`:
1. **Dẫn xuất khóa (Key Derivation):**
   * Sử dụng Master Key của hệ thống (lấy từ biến môi trường/Configuration) kết hợp với `WorkspaceId` để tạo ra **AES Key (32 bytes)** và **HMAC Key (64 bytes)** thông qua `HMACSHA512`.
2. **Mã hóa AES-256-CBC:**
   * Sinh một chuỗi ngẫu nhiên **IV (16 bytes)** bằng `RandomNumberGenerator`.
   * Sử dụng thuật toán AES-256 ở chế độ CBC mode để mã hóa luồng dữ liệu thô (Plaintext Stream) thành bản mã (Ciphertext).
3. **Tính toán HMAC-SHA512:**
   * Sử dụng **HMAC Key** để tính mã xác thực trên chuỗi kết hợp `[IV + Ciphertext]`. Kết quả trả về **64 bytes signature**.
4. **Lưu trữ xuống ổ đĩa:**
   * Ghi tuần tự: `IV` (16 bytes) + `Ciphertext` + `HMAC-SHA512` (64 bytes) thành tệp tin duy nhất tại đường dẫn cục bộ chỉ định bởi `StorageKey`.

### 3.2. Quy trình giải mã (Decrypt Flow)
Khi người dùng tải xuống tài liệu hoặc khi tiến trình AI Ingestion đọc nội dung file:
1. **Đọc tệp tin:**
   * Đọc 16 bytes đầu tiên làm `IV`.
   * Đọc 64 bytes cuối cùng làm `HMAC-SHA512` signature.
   * Phần ở giữa là `Ciphertext`.
2. **Kiểm tra tính toàn vẹn (Verify Integrity):**
   * Tính toán HMAC-SHA512 của chuỗi `[IV + Ciphertext]` bằng **HMAC Key**.
   * So sánh bằng thuật toán so sánh thời gian cố định (Constant-time comparison) với chữ ký đọc được ở cuối file. Nếu không khớp, **từ chối giải mã** ngay lập tức để tránh tấn công Padding Oracle.
3. **Giải mã AES-256-CBC:**
   * Khởi tạo bộ giải mã AES bằng `IV` và **AES Key**.
   * Giải mã `Ciphertext` thành luồng dữ liệu gốc (Plaintext Stream) và trả về cho ứng dụng.

### 3.3. Các phương án chống tải file rác, spam làm đứng hệ thống (Spam & Denial of Service Prevention)
Để bảo vệ tài nguyên máy chủ tránh hiện tượng thắt nút cổ chai, treo ứng dụng khi kẻ xấu cố tình upload các file dung lượng khổng lồ hoặc spam tệp tin rác:
1. **Kiểm soát kích thước tệp tin tối đa ở tầng Gateway/Web Server (Max Payload Size Limit):**
   * Cấu hình giới hạn kích thước request body tối đa **20MB** tại tầng **Gateway / Web Server (Kestrel / Nginx / YARP)** để hệ thống tự động ngắt kết nối và trả về mã lỗi `413 Payload Too Large` sớm ở mức hạ tầng mạng.
   * **Không phụ thuộc vào `Content-Length` trong Controller:** Tránh việc chỉ kiểm tra header `Content-Length` tại tầng ứng dụng, vì header này có thể bị bỏ qua (khi sử dụng chunked transfer encoding) hoặc bị giả mạo bởi kẻ tấn công. Việc chặn ở Gateway/Web Server đảm bảo lượng dữ liệu thực tế truyền qua đường truyền luôn được giới hạn cứng.
2. **Mã hóa và ghi trực tiếp dạng luồng (Zero-Buffer Streaming):**
   * Tuyệt đối không nạp toàn bộ file vào `byte[]` hay `MemoryStream` trong RAM. Việc đọc file từ Request, chạy qua luồng mã hóa `CryptoStream` và ghi xuống đĩa cục bộ phải được thực hiện hoàn toàn dưới dạng streaming (với buffer cố định nhỏ, ví dụ: 80KB).
   * Điều này đảm bảo độ phức tạp bộ nhớ là hằng số $O(1)$ Memory Complexity, ngăn chặn triệt để các lỗi tràn bộ nhớ (Out-Of-Memory) của server dù có hàng ngàn user upload đồng thời.
3. **Giới hạn tần suất yêu cầu (Rate Limiting):**
   * Cấu hình Rate Limiting trên API Gateway hoặc sử dụng Middleware: Giới hạn tần suất tải lên tài liệu (ví dụ: tối đa 5 file/phút đối với một tài khoản cá nhân, và tối đa 30 file/phút đối với một địa chỉ IP).
4. **Hạn mức lưu trữ theo Workspace (Storage Quota Enforcement):**
   * Đặt hạn mức lưu trữ tối đa cho mỗi Workspace (ví dụ: Enterprise Workspace được lưu tối đa 5GB tài liệu). Hệ thống sẽ kiểm tra dung lượng hiện tại trong DB trước khi cho phép ghi file mới xuống đĩa.
5. **Danh sách các loại file cho phép (File Extension & Mime-type Whitelisting):**
   * Chỉ chấp nhận các định dạng văn bản văn phòng tiêu chuẩn: `.pdf`, `.docx`, `.txt`. Từ chối lập tức các tệp tin thực thi nguy hiểm hoặc các tệp tin nén phức tạp (ví dụ `.zip`, `.tar.gz` để tránh tấn công "Zip bomb" làm đơ thư viện trích xuất).
6. **Xử lý AI Guardrails bất đồng bộ có giới hạn luồng (Asynchronous Processing with Concurrency Limit):**
   * Việc đọc nội dung file mã hóa và phân tích từ khóa nhạy cảm không chạy đồng bộ trong API chính. Thay vào đó, sự kiện được đẩy vào Redis Stream và xử lý bất đồng bộ qua [DocumentAiIngestionConsumerService](file:///c:/Users/Admin/Documents/WarpTalk%20-%20Capstone%20Project/warptalk-backend/workspace/src/WarpTalk.WorkspaceService.Infrastructure/BackgroundServices/DocumentAiIngestionConsumerService.cs) với số luồng chạy đồng thời bị giới hạn (ví dụ tối đa là 2 luồng xử lý cùng lúc trên mỗi instance). Điều này đảm bảo AI Service và CPU máy chủ không bị nghẽn (CPU Starvation) khi có lượng lớn file được đẩy vào.

---

## 4. Các điểm tích hợp mã nguồn (Integration Points)

### 4.1. Workspace Service (Application Layer)
* [WorkspaceDocumentService](file:///c:/Users/Admin/Documents/WarpTalk%20-%20Capstone%20Project/warptalk-backend/workspace/src/WarpTalk.WorkspaceService.Application/Services/WorkspaceDocumentService.cs):
  * Cần được tích hợp bộ mã hóa/giải mã khi ghi file thô từ `UploadDocumentAsync` hoặc đọc file trong `DownloadDocumentAsync`.
  * Khóa mã hóa `MasterKey` phải được cấu hình an toàn trong `appsettings.json` hoặc biến môi trường và được bảo vệ bằng ASP.NET Data Protection.

### 4.2. AI Ingestion Service (Infrastructure Layer)
* [DocumentAiIngestionConsumerService](file:///c:/Users/Admin/Documents/WarpTalk%20-%20Capstone%20Project/warptalk-backend/workspace/src/WarpTalk.WorkspaceService.Infrastructure/BackgroundServices/DocumentAiIngestionConsumerService.cs):
  * Khi tiến trình RAG thực hiện đọc file vật lý (quét nội dung tự động bằng AI), nó phải thực hiện giải mã tệp tin bằng khóa dẫn xuất tương ứng với `WorkspaceId` của tài liệu trước khi phân tích nội dung.

---

## 5. Kịch bản kiểm thử & Tiêu chí nghiệm thu (Acceptance Criteria)

- [ ] **Mã hóa thành công:** Tệp tin lưu ở đĩa local phải là định dạng nhị phân không thể đọc trực tiếp bằng trình soạn thảo văn bản thông thường.
- [ ] **Bảo vệ toàn vẹn dữ liệu:** Nếu thay đổi bất kỳ 1 byte nào trong file mã hóa tại local, quá trình tải xuống hoặc quét AI phải trả về lỗi xác thực toàn vẹn (HMAC mismatch) và từ chối xử lý.
- [ ] **Phân tách khóa giữa các Workspace:** Đảm bảo dữ liệu Workspace A không thể bị giải mã bằng khóa dẫn xuất của Workspace B.
- [ ] **AI Ingestion hoạt động bình thường:** Tiến trình chạy ngầm phải giải mã file thành công trong bộ nhớ RAM và thực hiện quét PII/DLP chính xác mà không ghi file plaintext xuống đĩa.

---

## 6. Hướng mở rộng và Đề xuất Kiến trúc cho hệ thống lớn (Future Scalability & Advanced Roadmap)

Để nâng cấp hệ thống đạt chuẩn Enterprise SaaS quy mô lớn, tránh các giới hạn tĩnh (hardcoded), các giải pháp mở rộng sau đây được đề xuất để tích hợp vào thiết kế trong tương lai:

### 6.1. Tải lên trực tiếp thông qua Presigned Upload URL
* **Thiết kế:** Thay vì tải tệp tin thông qua API của WarpTalk Backend, Backend sẽ đóng vai trò xác thực quyền, kiểm tra hạn mức và gọi S3/MinIO API để tạo một URL tải lên tạm thời (Presigned URL) có hiệu lực trong vòng 5-10 phút đi kèm ràng buộc kích thước (`content-length-range`).
* **Cách hoạt động:** Client (Browser/Desktop) nhận URL và tải file trực tiếp lên Storage Bucket. Sau khi tải lên thành công, Storage Provider kích hoạt webhook/sự kiện gửi về Backend để đăng ký tài liệu.
* **Lợi ích:** Giải phóng hoàn toàn tài nguyên CPU và băng thông mạng của máy chủ Backend, tận dụng năng lực phân phối và xử lý tải cao của Cloud Storage.

### 6.2. Cơ chế Chính sách Động (Dynamic Policy Engine)
* **Thiết kế:** Các tham số giới hạn như dung lượng tối đa (`MaxFileSizeMb`), danh sách định dạng đuôi được phép (`AllowedExtensions`) và tần suất tải lên (`RateLimits`) được lưu trữ trong Database theo cấp độ gói dịch vụ (Subscription Tier) hoặc cấu hình riêng của từng Workspace.
* **Cách hoạt động:** Các chính sách này được tải và lưu đệm trên Redis Cache. Khi người dùng yêu cầu upload, hệ thống sẽ kiểm tra động dựa trên gói dịch vụ đang hoạt động của Workspace đó.
* **Lợi ích:** Loại bỏ hoàn toàn các cấu hình tĩnh (hardcode), hỗ trợ cấu hình phân cấp (Multi-tenant) linh hoạt.

### 6.3. Xác thực loại tệp tin bằng Magic Bytes
* **Thiết kế:** Không chỉ kiểm tra phần mở rộng tệp tin (File Extension) hoặc header `Content-Type` do trình duyệt gửi lên. Tầng ứng dụng sẽ thực hiện đọc nhanh một số byte đầu tiên của luồng file (Magic Bytes) để xác định chữ ký định dạng thực tế của tệp tin.
* **Cách hoạt động:** Ví dụ: tệp tin PDF phải bắt đầu bằng `%PDF-`, DOCX/ZIP phải bắt đầu bằng `PK`. Nếu chữ ký byte không khớp với định dạng đăng ký, tệp tin bị từ chối ngay lập tức.
* **Lợi ích:** Ngăn chặn tuyệt đối việc giả mạo đuôi tệp tin (ví dụ đổi tên mã độc `.exe` thành `.pdf`) để tải lên hệ thống.

### 6.4. Tách biệt Quản lý khóa (External KMS / Key Vault)
* **Thiết kế:** Thay vì lưu Master Key trong cấu hình ứng dụng (`appsettings.json`), hệ thống sẽ tích hợp với các dịch vụ quản lý khóa chuyên nghiệp như **HashiCorp Vault**, **AWS KMS** hoặc **Azure Key Vault**.
* **Cách hoạt động:** Khi giải mã tài liệu, ứng dụng gửi yêu cầu lấy khóa mã hóa của Workspace tương ứng thông qua kết nối gRPC/API bảo mật được chứng thực chéo với Key Vault.
* **Lợi ích:** Nâng cao mức độ an toàn thông tin, đáp ứng các tiêu chuẩn tuân thủ bảo mật khắt khe như SOC2, ISO 27001.

### 6.5. Chống dịch ngược mã nguồn ứng dụng (Anti-Decompilation & Code Protection)
Đối với C#/.NET, bytecode (MSIL) mặc định chứa đầy đủ thông tin siêu dữ liệu (Metadata) giúp hacker dễ dàng dịch ngược về code C# gốc qua các công cụ như dnSpy hay ILSpy. Để bảo vệ các thuật toán mã hóa và logic sinh khóa:
1. **Biên dịch AOT gốc (Native AOT Compilation):**
   * **Thiết kế:** Biên dịch các dịch vụ Backend và Client bằng công nghệ Ahead-Of-Time (AOT) trực tiếp thành mã máy của hệ điều hành tương ứng (native machine binary).
   * **Lợi ích:** Loại bỏ toàn bộ bytecode trung gian (IL) và siêu dữ liệu metadata của C#. Khi hacker cố gắng dịch ngược, họ chỉ nhận được mã Assembly (CPU machine code), buộc phải sử dụng các công cụ dịch ngược mã máy phức tạp (Ghidra, IDA Pro) để phân tích, tăng độ khó tấn công lên gấp nhiều lần.
2. **Xáo trộn mã nguồn và Mã hóa chuỗi (Obfuscation & String Encryption):**
   * **Thiết kế:** Sử dụng các công cụ làm mờ mã (Obfuscators như *Obfuscar*, *Dotfuscator*, hoặc *VMProtect*).
   * **Lợi ích:** Tự động đổi tên các class, method, biến thành các ký tự không thể đọc được. Đồng thời mã hóa các chuỗi text tĩnh (SQL query, API endpoints, thông điệp bảo mật) và chỉ giải mã động trong RAM khi chạy để tránh bị scan tĩnh (static analysis).
3. **Ảo hóa mã nguồn (Virtual Machine Protection):**
   * **Thiết kế:** Đóng gói các hàm cốt lõi liên quan đến sinh khóa/mã hóa bằng các giải pháp ảo hóa tệp tin thực thi (như *VMProtect* hoặc *Themida*).
   * **Lợi ích:** Chuyển đổi mã máy sang một tập lệnh byte ngẫu nhiên riêng (Custom Bytecode) và thực thi qua một trình thông dịch ảo nhúng sẵn. Đây là rào cản lớn nhất ngăn chặn mọi trình gỡ lỗi (debugger) và dịch ngược dịch ngược tự động hiện tại.
