# Đặc tả Kiến trúc Bảo mật & Cô lập Dữ liệu Multi-Tenancy (WarpTalk)

Tài liệu này mô tả chi tiết giải pháp kỹ thuật nhằm cô lập dữ liệu giữa các doanh nghiệp (Enterprise Workspaces) và bảo toàn ngữ cảnh bảo mật người dùng xuyên suốt toàn bộ vòng đời của yêu cầu (đồng bộ qua HTTP/gRPC và bất đồng bộ qua Redis Streams / RabbitMQ).

---

## 1. Nguyên tắc thiết kế Cô lập dữ liệu (Multi-Tenancy Principles)

Hệ thống WarpTalk áp dụng mô hình **Logical Database Isolation (Cô lập Logic mức Database)**:
1. **Một Cơ sở dữ liệu vật lý (PostgreSQL)** duy nhất chứa toàn bộ dữ liệu, phân chia logic thành các schema riêng biệt cho từng service (`auth`, `workspace`, `translation_room`, `transcript`).
2. **Cô lập theo hàng (Row-level Isolation)**: Mọi bảng dữ liệu liên quan đến cấu trúc tổ chức hoặc tài nguyên hội họp (như tài liệu, bản ghi, cấu hình ngôn ngữ, lịch sử chat) bắt buộc phải có cột phân định `workspace_id`.
3. **Luồng dữ liệu khép kín**: Một người dùng (User) chỉ có quyền thực thi và truy xuất dữ liệu thuộc phạm vi Workspace mà họ đang hoạt động (Active Workspace). Mọi hành vi truy vấn xuyên Tenant mà không có quyền hệ thống (Platform Admin) đều bị coi là vi phạm an ninh thông tin.

---

## 2. Luồng Đồng bộ (Synchronous Flow: Gateway & gRPC)

Cơ chế truyền nhận và xác thực Identity của người dùng qua giao thức đồng bộ HTTP/gRPC được thực thi theo mô hình hai lớp bảo mật:

```
+-------------+  Bearer JWT  +-------------+  X-Internal-Context  +--------------------+
| Web/Mobile  |------------->| API Gateway |--------------------->| Domain Service API |
|   Client    |              |   (YARP)    |  (Ký số nội bộ)      | (Auth/Workspace...) |
+-------------+              +-------------+                      +--------------------+
```

### 2.1 Lớp ngoài: Client-to-Gateway (Bearer JWT)
- Client đính kèm Access Token tiêu chuẩn (Bearer JWT) nhận được từ dịch vụ Auth Service vào HTTP Header `Authorization`.
- API Gateway (YARP) chịu trách nhiệm xác thực tính hợp lệ của token (kiểm tra hạn dùng, chữ ký, cấu trúc).

### 2.2 Lớp trong: Service-to-Service (`X-Internal-Context` JWT)
Sau khi Gateway xác thực thành công danh tính người dùng và xác định được **Active Workspace** (thông qua đường dẫn route `/api/v1/workspaces/{slug}/...` hoặc cache session), nó sẽ sinh ra một Token nội bộ (Internal Context Token):
1. **Chữ ký số nội bộ (Signed JWT):** Token này được ký bằng thuật toán đối xứng **HMAC-SHA256** với mã khóa bí mật được cấu hình dùng chung trong hạ tầng mạng nội bộ (`JWT_SECRET_KEY` nội bộ).
2. **Cấu trúc Payload (Claims):**
   ```json
   {
     "sub": "3fa85f64-5717-4562-b3fc-2c963f66afa6",   // User ID
     "workspace_id": "8f8b8d96-c67d-411a-bbf6-b51f8a846bc0", // Active Tenant ID
     "role": "Member",                                 // Quyền hạn trong Workspace
     "membership_type": "internal",                    // Loại thành viên
     "exp": 1782163200                                 // Thời gian hết hạn cực ngắn (ví dụ: 1-5 phút)
   }
   ```
3. **Header đính kèm:** `X-Internal-Context: Bearer <Internal_JWT_String>`

### 2.3 Middleware xử lý tại Backend Downstream (`InternalContextMiddleware`)
Mỗi downstream microservice khi nhận yêu cầu HTTP/gRPC sẽ quét Header `X-Internal-Context`:
- Validate chữ ký số bằng Shared Secret. Nếu không hợp lệ hoặc hết hạn, từ chối yêu cầu bằng HTTP `401 Unauthorized`.
- Trích xuất các Claims và ánh xạ vào Service DI Container dưới dạng đối tượng có vòng đời Request (Scoped):
  ```csharp
  public interface IWorkspaceContext
  {
      Guid? WorkspaceId { get; }
      Guid? UserId { get; }
      string Role { get; }
      string MembershipType { get; }
      bool IsAuthenticated { get; }
  }
  ```

---

## 3. Luồng Bất đồng bộ (Asynchronous Flow: Message Broker & Streams)

Đối với các tác vụ chạy nền bất đồng bộ (ví dụ: client gửi audio chunk qua SignalR Hub rồi đẩy vào Redis Streams, hoặc upload tài liệu rồi kích hoạt hàng đợi RabbitMQ), hệ thống không có HTTP Header để truyền `X-Internal-Context`.

### 3.1 Quy chuẩn tự đóng gói ngữ cảnh (Context Self-Packaging)
Mọi Message được sản xuất (Publish) vào Redis Streams hoặc RabbitMQ **bắt buộc phải đính kèm thông tin nhận diện Workspace và User** ngay trong nội dung Payload của tin nhắn.

#### Ví dụ với Payload của Audio Chunk (`Redis Streams`):
```json
{
  "room_id": "4fa85f64-5717-4562-b3fc-2c963f66afa6",
  "speaker_id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "workspace_id": "8f8b8d96-c67d-411a-bbf6-b51f8a846bc0", // Bắt buộc
  "chunk_index": 12,
  "audio_data": "base64...",
  "timestamp_ms": 1782163200000
}
```

#### Ví dụ với Payload của Event Ingest Document (`RabbitMQ`):
```json
{
  "document_id": "2fa85f64-5717-4562-b3fc-2c963f66afa6",
  "workspace_id": "8f8b8d96-c67d-411a-bbf6-b51f8a846bc0", // Bắt buộc
  "uploaded_by_user_id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "storage_path": "documents/2026/06/doc.pdf"
}
```

### 3.2 Khởi tạo Scoped Context tại Background Consumer
Khi một dịch vụ nền (Background Service / Queue Consumer) tiêu thụ tin nhắn:
1. Tạo một **Dependency Injection Scope** mới (`IServiceProvider.CreateScope()`).
2. Trích xuất `workspace_id` và `user_id` từ tin nhắn.
3. Gán thủ công các giá trị này vào một implementation chuyên biệt của `IWorkspaceContext` (ví dụ: `AppWorkspaceContext`) trong Scope vừa tạo.
4. Chạy logic xử lý nghiệp vụ bên trong Scope đó. Lúc này, DbContext và các Service hạ tầng sẽ tự động nhận biết được Workspace hiện tại một cách an toàn.

---

## 4. Bảo mật mức Cơ sở dữ liệu với EF Core Global Query Filters

Để đảm bảo các lập trình viên không vô tình truy vấn nhầm dữ liệu của Workspace khác (SQL Leakage), WarpTalk tích hợp **Global Query Filters** vào các lớp DbContext của C# Entity Framework Core.

### 4.1 Cơ chế hoạt động
Khi Global Query Filter được kích hoạt cho thực thể (Entity) có trường `WorkspaceId`, EF Core sẽ tự động chèn thêm điều kiện:
```sql
WHERE workspace_id = @__current_workspace_id
```
vào tất cả các câu lệnh SQL sinh ra (truy vấn đơn, JOIN, Include, v.v.).

### 4.2 Triển khai trong Mã nguồn C#

#### Định nghĩa Base Entity chia sẻ Workspace:
```csharp
public abstract class WorkspaceEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; } // Phục vụ phân vùng dữ liệu
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

#### Cấu hình DbContext áp dụng Filter:
```csharp
using Microsoft.EntityFrameworkCore;

public class WorkspaceDbContext : DbContext
{
    private readonly IWorkspaceContext _workspaceContext;

    public WorkspaceDbContext(
        DbContextOptions<WorkspaceDbContext> options,
        IWorkspaceContext workspaceContext) : base(options)
    {
        _workspaceContext = workspaceContext;
    }

    public DbSet<WorkspaceDocument> Documents { get; set; }
    public DbSet<WorkspaceMember> Members { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Áp dụng Global Query Filter cho tất cả Entity kế thừa từ WorkspaceEntity
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(WorkspaceEntity).IsAssignableFrom(entityType.ClrType))
            {
                // Sử dụng lambda expression động để trích xuất WorkspaceId từ IWorkspaceContext
                modelBuilder.Entity(entityType.ClrType)
                    .HasQueryFilter(CreateWorkspaceFilterExpression(entityType.ClrType));
            }
        }
    }

    private LambdaExpression CreateWorkspaceFilterExpression(Type type)
    {
        // Tạo biểu thức: entity => entity.WorkspaceId == _workspaceContext.WorkspaceId
        var parameter = Expression.Parameter(type, "entity");
        var property = Expression.Property(parameter, nameof(WorkspaceEntity.WorkspaceId));
        
        // Trỏ tới WorkspaceId trong Scoped Context
        var contextProp = Expression.Property(Expression.Constant(this), nameof(_workspaceContext));
        var workspaceIdProp = Expression.Property(contextProp, nameof(IWorkspaceContext.WorkspaceId));
        
        // So sánh
        var body = Expression.Equal(property, workspaceIdProp);
        return Expression.Lambda(body, parameter);
    }
}
```

### 4.3 Cách ghi đè (Bỏ qua Query Filter) khi cần thiết
Trong một số tình huống đặc thù (ví dụ: tác vụ Admin hệ thống cần thống kê dữ liệu toàn bộ nền tảng, hoặc tiến trình chạy nền dọn dẹp tài nguyên hết hạn toàn cục), lập trình viên có thể bỏ qua bộ lọc bằng phương thức `IgnoreQueryFilters()`:

```csharp
// Lấy toàn bộ tài liệu bất kể Workspace nào (Chỉ dùng cho hệ thống hoặc Platform Admin)
var allDocuments = await _dbContext.Documents
    .IgnoreQueryFilters()
    .ToListAsync(cancellationToken);
```

---

## 5. Các lỗ hổng tiềm ẩn & Cách giảm thiểu (Security Mitigations)

1. **Giả mạo dữ liệu Payload (Payload Spoofing):**
   - *Rủi ro:* Kẻ xấu có thể can thiệp vào Redis Stream nội bộ và gửi tin nhắn mang `workspace_id` giả mạo để ghi đè dữ liệu.
   - *Giảm thiểu:* Đảm bảo an toàn kết nối mạng nội bộ. Redis và RabbitMQ không được public ra Internet (chỉ listen trên mạng nội bộ Docker Network / Kubernetes Cluster). Các thông tin kết nối phải được bảo vệ bằng mật khẩu mạnh thông qua Docker Secrets/Vault.
2. **Quên gán WorkspaceId khi thêm mới thực thể:**
   - *Rủi ro:* Bản ghi mới chèn vào không có `workspace_id`, hoặc mang giá trị sai lệch.
   - *Giảm thiểu:* Override phương thức `SaveChanges` / `SaveChangesAsync` của DbContext để tự động điền `WorkspaceId` từ `IWorkspaceContext` trước khi lưu vào cơ sở dữ liệu đối với mọi thực thể kế thừa từ `WorkspaceEntity`.

```csharp
public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
{
    foreach (var entry in ChangeTracker.Entries<WorkspaceEntity>())
    {
        if (entry.State == EntityState.Added)
        {
            if (_workspaceContext.WorkspaceId == null || _workspaceContext.WorkspaceId == Guid.Empty)
            {
                throw new InvalidOperationException("Không thể lưu bản ghi vì thiếu thông tin Active Workspace.");
            }
            entry.Entity.WorkspaceId = _workspaceContext.WorkspaceId.Value;
        }
    }
    return base.SaveChangesAsync(cancellationToken);
}
```
