# WarpTalk B2B Billing & Payment Implementation Report

Bản báo cáo này cung cấp thông tin đối chiếu chi tiết giữa tài liệu **WarpTalk B2B Billing Plan** với hiện trạng triển khai trong source code của hai phân hệ `WarpTalk.BillingService` và `WarpTalk.PaymentService` trong `warptalk-backend`.

---

## 1. Trạng Thái Đáp Ứng Yêu Cầu Nghiệp Vụ

| Nghiệp vụ (B2B Billing Plan) | Trạng thái | Chi tiết triển khai trong Source Code |
| :--- | :---: | :--- |
| **Mô hình ví dùng chung cấp Workspace**<br>*(Workspace-pooled Credits)* | **Hoàn thành** | Tất cả API kiểm tra số dư (`GetWorkspaceCredits`), trừ tiền (`ConsumeCredits`, `RecordUsage`) và đặt trước (`ReserveCredits`) đều được xác thực và cập nhật số dư dựa trên `WorkspaceId` thay vì ID cá nhân, đảm bảo toàn bộ thành viên dùng chung ví credits. |
| **Cơ cấu gói dịch vụ (Tiers)**<br>• Startup: 190.000đ (30.000 credits)<br>• Enterprise: 490.000đ (100.000 credits) | **Hoàn thành** | Đã định cấu hình gói tại database thông qua script [patch-plans.sql](file:///d:/Warptalk/patch-plans.sql). Cập nhật model [Plan.cs](file:///d:/Warptalk/warptalk-backend/billing/src/WarpTalk.BillingService.Domain/Entities/Plan.cs) để lưu trữ hạn mức voice clone (`VoiceCloneLimitMins`) và các cờ phân quyền (`AllowGlossary`, `AllowAcl`). |
| **Giới hạn Voice Cloning gói Startup**<br>*(Tối đa 120 phút/tháng, tự động fallback)* | **Hoàn thành** | Triển khai hàm `GetVoiceCloneMinutesUsedThisCycleAsync` trong [CreditAndUsageService.cs](file:///d:/Warptalk/warptalk-backend/billing/src/WarpTalk.BillingService.Application/Services/CreditAndUsageService.cs) để tính tổng số giây voice clone đã sử dụng trong chu kỳ hiện tại. Nếu vượt quá 120 phút, hệ thống từ chối và trả về lỗi mã `VOICE_CLONE_LIMIT_EXCEEDED` để AI pipeline tự động fallback sang Standard TTS. |
| **Định mức tiêu thụ Credits (Minute-based)**<br>• STT: 10 credits/phút<br>• Translation: 10 credits/phút/ngôn ngữ<br>• Standard TTS: 5 credits/phút<br>• Voice Cloning: 25 credits/phút | **Hoàn thành** | Viết lại hàm `CalculateCreditCost` trong [CreditAndUsageService.cs](file:///d:/Warptalk/warptalk-backend/billing/src/WarpTalk.BillingService.Application/Services/CreditAndUsageService.cs). Chi phí được tính chính xác dựa trên thời gian chạy (audioSeconds) nhân với tổng mức phí các tác vụ AI đang chạy (STT, Translation, TTS). |
| **Nạp tiền Top-up qua Stripe & Chiết khấu lũy tiến**<br>• Tỷ giá chuẩn: 1 Credit = 10 VND<br>• Nạp $\ge$ 10k credits: Chiết khấu 10%<br>• Nạp $\ge$ 25k credits: Chiết khấu 15%<br>• Nạp $\ge$ 50k credits: Chiết khấu 20% | **Hoàn thành** | Cập nhật hàm xử lý thanh toán thành công `ProcessPaymentSuccessInternal` tại [BillingGrpcService.cs](file:///d:/Warptalk/warptalk-backend/billing/src/WarpTalk.BillingService.API/GrpcServices/BillingGrpcService.cs). Khi phát hiện `PaymentType` là `"CreditTopUp"`, hệ thống sẽ quy đổi từ số tiền thanh toán thực tế (VND) sang số lượng credits tương ứng với các bậc chiết khấu lũy tiến. |
| **Bảo mật Glossary & ACL nâng cao**<br>*(Chỉ mở khóa ở gói Enterprise)* | **Hoàn thành** | Quyền hạn được quản lý tập trung thông qua API check quyền `GetWorkspaceFeatureAccess` sử dụng các cờ thuộc tính đã ánh xạ từ Plan. Gói Enterprise sẽ được kích hoạt quyền tạo Glossary và phân quyền tài liệu. |
| **Chính sách hoàn trả B2B (Manual Refund)** | **Hoàn thành** | Hỗ trợ admin hoàn trả lại credits thủ công thông qua API `AdjustCreditsAsync` đã được tích hợp đầy đủ cơ chế lưu vết (Audit Trail / Reference ID) để đối soát lỗi hệ thống khi có yêu cầu hoàn trả qua support ticket. |

---

## 2. Chi Tiết Các Thay Đổi Trọng Yếu Trong Source Code

### 2.1. Cấu hình Model & Database Mapping
* **File:** [Plan.cs](file:///d:/Warptalk/warptalk-backend/billing/src/WarpTalk.BillingService.Domain/Entities/Plan.cs)
  * Bổ sung thuộc tính: `VoiceCloneLimitMins`, `AllowGlossary`, `AllowAcl`.
* **File:** [BillingDbContext.cs](file:///d:/Warptalk/warptalk-backend/billing/src/WarpTalk.BillingService.Infrastructure/Persistence/Contexts/BillingDbContext.cs)
  * Ánh xạ các cột cơ sở dữ liệu `voice_clone_limit_mins`, `allow_glossary`, `allow_acl`.

### 2.2. Xử lý Logic & Tiêu thụ Credits
* **File:** [CreditAndUsageService.cs](file:///d:/Warptalk/warptalk-backend/billing/src/WarpTalk.BillingService.Application/Services/CreditAndUsageService.cs)
  * Thiết kế lại `CalculateCreditCost` áp dụng biểu phí tính theo phút (Minute-based pricing).
  * Viết thêm hàm helper `GetVoiceCloneMinutesUsedThisCycleAsync` để truy vấn thời gian dùng voice clone thực tế.
  * Tích hợp cơ chế chặn `VOICE_CLONE_LIMIT_EXCEEDED` tại `ReserveCreditsAsync` và `RecordUsageAsync`.

### 2.3. Quy đổi Stripe Top-Up thành Credits
* **File:** [BillingGrpcService.cs](file:///d:/Warptalk/warptalk-backend/billing/src/WarpTalk.BillingService.API/GrpcServices/BillingGrpcService.cs)
  * Phân luồng giữa gia hạn định kỳ (`SubscriptionRenewal` / `SubscriptionPurchase`) và nạp tiền lẻ (`CreditTopUp`).
  * Thực hiện quy đổi số tiền VND sang credits dựa theo các ngưỡng chiết khấu lũy tiến:
    * $\ge$ 400.000 VND (50k credits) $\rightarrow$ 8 VND / credit.
    * $\ge$ 212.500 VND (25k credits) $\rightarrow$ 8.5 VND / credit.
    * $\ge$ 90.000 VND (10k credits) $\rightarrow$ 9 VND / credit.
    * Dưới 90.000 VND $\rightarrow$ 10 VND / credit.

### 2.4. Unit Tests Bổ Sung
* **File:** [CreditServiceTests.cs](file:///d:/Warptalk/warptalk-backend/billing/tests/WarpTalk.BillingService.Tests/Application/Services/CreditServiceTests.cs)
  * `RecordUsageAsync_VoiceCloneLimitExceeded_ShouldReturnFailure`
  * `ReserveCreditsAsync_VoiceCloneLimitExceeded_ShouldReturnFailure`
* **File:** [RealtimeCostCalculatorTests.cs](file:///d:/Warptalk/warptalk-backend/billing/tests/WarpTalk.BillingService.Tests/Application/Services/RealtimeCostCalculatorTests.cs)
  * Điều chỉnh kỳ vọng các case thử nghiệm tương ứng với cấu trúc tính phí theo phút mới.

---

## 3. Khảo Sát Tích Hợp Hệ Thống & Điểm Cần Lưu Ý

1. **Chu kỳ đăng ký (Monthly & Yearly):** Vẫn được lưu giữ độc lập và kiểm soát trọn vẹn trong luồng subscription thông thường.
2. **Hạn mức Voice Cloning:** Hoạt động hoàn toàn qua cơ chế lỗi `VOICE_CLONE_LIMIT_EXCEEDED`, đảm bảo các worker AI có thể tự động fallback sang Standard TTS mà không cần sửa code ở các service khác.
3. **Mã Stripe Price ID:** Đảm bảo đăng ký khớp mã sản phẩm và Price ID trên hệ thống Stripe Dashboard để đồng bộ hóa đơn thanh toán cho Subscription.
