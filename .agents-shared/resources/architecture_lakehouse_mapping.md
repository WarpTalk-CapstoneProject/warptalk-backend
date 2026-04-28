# Kiến trúc WarpTalk x Data Lakehouse

Dựa trên ý tưởng đối chiếu hệ thống WarpTalk với kiến trúc Enterprise Data Lakehouse (như Databricks/Delta Lake), dưới đây là bản vẽ kiến trúc sơ bộ để chúng ta cùng brainstorm. 

Biểu đồ này tuân thủ nguyên tắc **Tách biệt Tính toán và Lưu trữ (Decoupled Compute and Storage)**, đồng thời làm nổi bật khả năng xử lý **Polyglot Persistence** (Đa lưu trữ) và **Diverse Workloads** (Đa dạng tác vụ đầu ra).

```mermaid
flowchart LR
    ClientApp["📱 Client App (Meeting)"]
    ClientWeb["💻 Web Portal (Admin)"]

    Src1["🎤 Phi cấu trúc (Audio Chunks)"]
    Src2["📄 Bán cấu trúc (Webhooks, Logs)"]
    Src3["📊 Có cấu trúc (User, Gói cước)"]

    GW["🎛️ API Gateway (YARP + SignalR)"]
    Services["⚙️ .NET Microservices"]
    Broker[("🔄 Redis Broker")]
    KEDA["⚓ K8s + KEDA (Auto-scaler)"]
    Workers["🧠 Python AI Cluster"]

    PG[("🐘 PostgreSQL")]
    Vec[("🎯 Vector DB")]
    S3[("☁️ S3 Storage")]

    RT["⚡ Real-time Ops (Virtual Audio)"]
    Async["🤖 Analytics (Meeting Insights)"]
    BI["📈 BI Dashboard (Báo cáo)"]

    subgraph Frontend ["0. Client Apps"]
        ClientApp
        ClientWeb
    end

    subgraph Ingestion ["1. Data Ingestion"]
        Src1
        Src2
        Src3
    end

    subgraph Compute ["3. Decoupled Compute"]
        GW
        Services
        Broker
        KEDA
        Workers
    end

    subgraph Storage ["2. Polyglot Storage"]
        PG
        Vec
        S3
    end

    subgraph Workloads ["4. Diverse Workloads"]
        RT
        Async
        BI
    end

    ClientApp -- "Mic Stream" --> Src1
    ClientWeb -- "User Action" --> Src3
    
    Src1 -- "WebSocket" --> GW
    Src2 -- "HTTP/JSON" --> GW
    Src3 -- "REST API" --> GW

    GW -- "Route" --> Services
    Services -- "ACID Write" --> PG
    Services -- "Upload" --> S3

    GW -- "Push Task" --> Broker
    Broker -- "Pull Task" --> Workers
    Workers -- "Return Result" --> Broker
    Broker -- "Return Result" --> GW

    Broker -. "Monitor Queue" .-> KEDA
    KEDA -. "Auto-Scale" .-> Workers

    Workers -- "Vector Search" --> Vec
    Workers -- "Save Summary" --> S3
    Workers -- "Update DB" --> PG

    GW -- "Stream" --> RT
    Workers -- "Background" --> Async
    Services -- "Query" --> BI
    
    RT -- "Virtual Audio" --> ClientApp
    Async -- "View Insights" --> ClientWeb
    BI -- "View Dashboard" --> ClientWeb
```

## Giải thích luồng dữ liệu theo góc nhìn Lakehouse:

1. **Ingestion (Bơm dữ liệu vào):** .NET Gateway đóng vai trò như một phễu hứng mọi loại data (từ âm thanh real-time đến các thao tác click chuột). Gateway **không trực tiếp tính toán AI** mà chỉ làm nhiệm vụ xác thực, phân loại và lưu trữ tạm thời.
2. **Storage (Lưu trữ như Delta Lake):** Dữ liệu thô lập tức được phân bổ về đúng nơi lưu trữ phù hợp (Postgres cho giao dịch tiền nong, S3 cho file gốc để lưu vết, VectorDB cho các embedding siêu chiều). Tính chất "Lake" thể hiện ở việc chúng ta giữ lại toàn bộ data thô (raw audio, webhook payload).
3. **Compute (Tính toán như Databricks):** Khi có cuộc họp, Gateway đẩy task qua Redis/gRPC. Cụm AI Workers (Python) sẽ được bật lên (hoặc scale out) để kéo dữ liệu từ Storage và Broker về xử lý. Tính toán xong, chúng ghi kết quả ngược lại Storage hoặc trả về Broker rồi rảnh rỗi. Nếu sập Worker, dữ liệu ở Storage vẫn an toàn.
4. **Workloads (Tiêu thụ):** Dữ liệu sau khi xử lý được phục vụ cho 3 mục đích hoàn toàn khác nhau (Real-time stream, Phân tích chuyên sâu, và Lên biểu đồ báo cáo).

---
## Cơ chế Auto-Scale của cụm Python AI Cluster

Điểm sáng giá nhất của kiến trúc này là khả năng mở rộng cụm AI hoàn toàn độc lập nhờ cơ chế giao tiếp qua Message Broker (Redis Streams/gRPC).

1. **Phi trạng thái (Statelessness):** Các Python Workers (STT, LLM, Voice Cloning) không lưu giữ state của cuộc họp. Chúng chỉ có một nhiệm vụ duy nhất: Kéo (pull) 1 chunk audio từ Redis -> Tính toán (bằng GPU) -> Trả kết quả lại Redis.
2. **Consumer Groups (Cân bằng tải tự động):** Các AI worker cùng loại sẽ tham gia vào chung một `Consumer Group` trong Redis. Nếu Gateway đẩy 100 task vào hàng đợi và bạn đang có 5 AI worker, Broker sẽ tự động chia đều task cho 5 worker này (Round-robin) mà không bắt Gateway phải biết IP hay tự điều phối.
3. **Scale theo chiều ngang (Scale-out):** Khi số lượng phòng họp tăng đột biến, lượng task đổ vào hàng đợi (queue) sẽ tăng vọt. Một hệ thống điều phối (như Kubernetes HPA) chỉ cần theo dõi "độ dài của hàng đợi". Queue càng dài -> tự động bật thêm (spawn) các container Python AI mới. Worker mới vừa boot xong sẽ lập tức cắm vào Redis kéo task phụ giúp ngay.
4. **Scale to Zero (Tối ưu chi phí):** Khi tất cả cuộc họp kết thúc, hàng đợi trống không. Orchestrator sẽ tự động tắt sạch cụm AI (vốn ngốn rất nhiều tiền thuê GPU). Trong lúc đó, cụm `.NET Gateway`, `Database` và `React Web` (rất nhẹ và rẻ) vẫn thức 24/7 để phục vụ người dùng đăng nhập và xem lại lịch sử.

> [!TIP]
> **Chốt hạ trước hội đồng:** Nhờ sự tách biệt hoàn toàn Compute (AI) khỏi Control Plane (.NET Gateway), WarpTalk giải quyết được bài toán hóc búa nhất của AI Streaming: **Chịu tải vô hạn khi có hàng nghìn người họp cùng lúc (chỉ cần scale thêm worker), nhưng lại tối ưu chi phí xuống mức 0 cho cụm AI khi hệ thống nhàn rỗi.**

---

## Phụ lục 1: Kiến trúc DevOps Pipeline (CI/CD)

Để triển khai và vận hành mượt mà kiến trúc đồ sộ như trên, hệ thống cần một quy trình tự động hóa (CI/CD) chuẩn mực. Dưới đây là luồng DevOps từ lúc code đến lúc đẩy lên Production (Kubernetes).

```mermaid
flowchart LR
    Dev["👨‍💻 Developers"]
    Git["🐙 GitHub Repo"]
    BuildDotNet["⚙️ Build .NET"]
    BuildPy["🐍 Build Python AI"]
    BuildWeb["⚛️ Build ReactJS"]
    Reg[("📦 Container Registry")]
    Argo["🐙 ArgoCD / GitOps"]
    K8s_GW["🎛️ Gateway / Services"]
    K8s_AI["🧠 AI Workers"]
    K8s_DB[("🐘 DB / Redis / KEDA")]
    Mon["📊 Monitor (Grafana)"]

    subgraph SCM ["1. Source Control"]
        Dev
        Git
    end

    subgraph CI ["2. Continuous Integration"]
        BuildDotNet
        BuildPy
        BuildWeb
    end

    subgraph CD ["3. Continuous Deployment"]
        Argo
    end

    subgraph Prod ["4. Kubernetes Cluster"]
        K8s_GW
        K8s_AI
        K8s_DB
        Mon
    end

    Dev -- "Push Code" --> Git
    
    Git -- "Trigger" --> BuildDotNet
    Git -- "Trigger" --> BuildPy
    Git -- "Trigger" --> BuildWeb

    BuildDotNet -- "Push Image" --> Reg
    BuildPy -- "Push Image" --> Reg
    BuildWeb -- "Push Image" --> Reg

    Reg -- "Pull Image" --> Argo
    Argo -- "Deploy" --> K8s_GW
    Argo -- "Deploy" --> K8s_AI
    
    K8s_GW -. "Connect" .-> K8s_DB
    K8s_AI -. "Connect" .-> K8s_DB
    
    Prod -- "Metrics" --> Mon
```

---

## Phụ lục 2: Chi tiết luồng dữ liệu của AI Workers (Data Plane)

Biểu đồ này "zoom" kỹ vào cụm `Compute` để thấy rõ cách Gateway và các AI Workers giao tiếp hoàn toàn phi trạng thái (Stateless) thông qua các hàng đợi Redis Streams. 

```mermaid
flowchart TD
    Gateway["🎛️ .NET Gateway (SignalR)"]
    
    stt_q[("stt:tasks")]
    stt_res[("stt:results")]
    trans_q[("translate:tasks")]
    trans_res[("translate:results")]
    tts_q[("tts:tasks")]
    tts_res[("tts:results")]
    ai_res[("ai_assistant:results")]

    STT["🎙️ STT Worker (Whisper)"]
    LLM["📝 Translate Worker (Llama)"]
    TTS["🗣️ Voice Clone Worker (XTTS)"]
    Assistant["🤖 AI Assistant Worker"]

    subgraph Redis ["🔄 Redis Streams (Message Broker)"]
        stt_q
        stt_res
        trans_q
        trans_res
        tts_q
        tts_res
        ai_res
    end
    
    subgraph WorkersCluster ["🧠 Python AI Cluster"]
        STT
        LLM
        TTS
        Assistant
    end
    
    Gateway -- "Push Audio" --> stt_q
    
    stt_q -- "Pull" --> STT
    STT -- "Push Text" --> stt_res
    STT -- "Trigger Translate" --> trans_q
    
    trans_q -- "Pull" --> LLM
    LLM -- "Push Translation" --> trans_res
    LLM -- "Trigger Voice" --> tts_q
    
    tts_q -- "Pull" --> TTS
    TTS -- "Push Audio" --> tts_res
    
    stt_res -- "Listen" --> Assistant
    Assistant -- "Action Items" --> ai_res
    
    stt_res -- "SignalR Update" --> Gateway
    trans_res -- "SignalR Update" --> Gateway
    tts_res -- "Virtual Audio" --> Gateway
    ai_res -- "Notifications" --> Gateway
```

---

## Phụ lục 3: Kiến trúc Microservices & Luồng Real-time SignalR

Sơ đồ này mô tả chi tiết cách Gateway phân luồng dữ liệu (Routing) đến các Microservices và cách hệ thống quản lý kết nối Real-time (SignalR) với hàng ngàn thiết bị Client thông qua Redis.

```mermaid
flowchart TD
    Client["💻 Client (React / Desktop)"]
    
    YARP["🎛️ YARP Proxy (REST API)"]
    Hub["📡 SignalR Hub (WebSocket)"]
    
    Auth["🔐 Auth Service (JWT)"]
    Room["🏢 Room Service (Meetings)"]
    Transcript["📝 Transcript Service"]
    Notif["🔔 Notification Service"]
    
    RedisBackplane[("🔄 Redis (Message Broker & Backplane)")]
    Workers["🧠 AI Workers (Python)"]
    
    subgraph GatewayGroup ["Gateway Layer (Scale out)"]
        YARP
        Hub
    end
    
    subgraph Microservices ["Business Modules (.NET)"]
        Auth
        Room
        Transcript
        Notif
    end
    
    Client -- "WebSocket Connection" --> Hub
    Client -- "REST Request" --> YARP
    
    YARP -- "Route /api/auth" --> Auth
    YARP -- "Route /api/room" --> Room
    YARP -- "Route /api/transcript" --> Transcript
    
    Hub -- "Subscribe Room Group" --> RedisBackplane
    
    Workers -- "Publish AI Result" --> RedisBackplane
    Room -- "Publish Room Event" --> RedisBackplane
    Notif -- "Publish Notification" --> RedisBackplane
    
    RedisBackplane -- "Consume Stream" --> Hub
    Hub -- "Push Message (Room ID)" --> Client
```
