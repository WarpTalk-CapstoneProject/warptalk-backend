# Sơ đồ Triển khai Kiến trúc Hệ thống (WarpTalk Deployment Architecture)

Dựa trên quyết định chốt phương án **Azure AKS (cho Backend & AI)** và **Vercel (cho Frontend)**, đây là sơ đồ luồng dữ liệu và tổ chức hạ tầng (Infrastructure) chi tiết.

```mermaid
flowchart LR
    User((Người dùng))

    subgraph Vercel ["Vercel Cloud"]
        Frontend("Frontend (Next.js/React)")
    end

    subgraph Azure ["Microsoft Azure"]
        subgraph AKS ["Azure Kubernetes Service (Kiến trúc Ultra-Saving)"]
            
            subgraph CPUPool ["System Node Pool (CPU Cố định - 24/7)"]
                Gateway["YARP API Gateway"]
                Auth["Auth Service"]
                TransRoom["Translation Room Service"]
                Transcript["Transcript Service"]
                Notif["Notification Service"]
                Coturn["Coturn (WebRTC)"]
                Redis[("Redis Cache (Queue)")]
                KEDA{"KEDA Autoscaler"}
            end

            subgraph GPUPool ["User Node Pool (GPU Spot - Scale to Zero)"]
                AI_STT["AI STT Worker"]
                AI_TTS["AI TTS Worker"]
                AI_Trans["AI Translation Worker"]
                AI_Assistant["AI Assistant Worker"]
                Qdrant[("Qdrant Vector DB")]
            end
        end

        subgraph ManagedServices ["Azure Managed Services"]
            Postgres[("Azure Database for PostgreSQL")]
            SignalR{"Azure SignalR Service"}
            AppInsights("Application Insights")
        end
    end

    %% Connections
    User -->|Truy cập| Frontend
    Frontend -->|REST API| Gateway
    Frontend -.->|WebSocket| SignalR
    Frontend -.->|P2P/Relay| Coturn

    Gateway -->|HTTP/1| Auth
    Gateway -->|HTTP/1| TransRoom
    Gateway -->|HTTP/1| Transcript
    Gateway -->|HTTP/1| Notif

    Auth -->|EF Core| Postgres
    TransRoom -->|EF Core| Postgres
    Transcript -->|EF Core| Postgres
    Notif -->|EF Core| Postgres

    TransRoom -->|Pub/Sub| Redis
    Notif -->|Pub/Sub| Redis

    Redis -->|Lấy Job| AI_STT
    Redis -->|Lấy Job| AI_TTS
    Redis -->|Lấy Job| AI_Trans
    Redis -->|Lấy Job| AI_Assistant

    AI_Assistant -->|Truy vấn| Qdrant

    Auth -.->|Gửi Log| AppInsights
    TransRoom -.->|Gửi Log| AppInsights

    Gateway -.->|Scale| SignalR

    %% KEDA Auto-scaling logic
    KEDA -.->|1. Đo lường Hàng đợi Job| Redis
    KEDA ===>|2. Bật/Tắt Container tự động| GPUPool
```

### Chú thích luồng hoạt động (Data Flow):
1. **Truy cập UI:** Người dùng truy cập vào Domain, tải giao diện Frontend siêu tốc từ mạng lưới CDN toàn cầu của **Vercel**.
2. **Gọi API:** Từ Vercel (trình duyệt của User), Frontend gửi các HTTP REST Request đâm thẳng vào địa chỉ Public IP của **YARP API Gateway** đang chạy trên CPU Node Pool của cụm AKS.
3. **Định tuyến (Routing):** Gateway điều phối request tới các .NET Microservices tương ứng (Auth, Translation Room, Transcript). Các Service này lưu/đọc dữ liệu từ **Azure Managed PostgreSQL**.
4. **Xử lý AI Âm thanh (GPU):**
   - Khi có file âm thanh từ Frontend gửi lên, Backend đẩy thông điệp vào **Redis Queue** (nằm ở CPU Pool).
   - Các **AI Workers** (Python) đang túc trực bên cụm GPU Node Pool lập tức "chộp" lấy thông điệp từ Redis, tận dụng sức mạnh card NVIDIA T4 để dịch thuật nhanh nhất có thể.
   - Khi dịch xong, AI Worker đẩy kết quả ngược lại Redis. Backend nhận kết quả và bắn về Frontend qua **Azure SignalR**.
5. **Giám sát (Monitoring):** Toàn bộ log và số liệu hoạt động của hệ thống được đẩy về **Application Insights** để vẽ biểu đồ và cảnh báo lỗi.
