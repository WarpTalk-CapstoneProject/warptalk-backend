# Qdrant Schema Design & Best Practices

**Date**: 2026-04-24
**Topic**: Qdrant Vector DB Configuration & Indexing
**Context**: Qdrant is schema-less for payloads but requires strict configuration for Vectors and explicit indexing for efficient filtering. Đảm bảo logic này được thực thi ở repo `warptalk-ai` thay vì backend.

## 1. Vector Configuration (Bắt buộc)
Khi tạo một Qdrant Collection (tương đương với Table), BẮT BUỘC phải định nghĩa cấu trúc của vector. Qdrant sẽ báo lỗi nếu insert vector sai cấu trúc.
- **Vector Size**: Phải khớp chính xác với output dimension của mô hình Embedding AI.
  - OpenAI `text-embedding-3-small`: 1536
  - Mã nguồn mở `all-MiniLM-L6-v2`: 384
- **Distance Metric (Thước đo)**: 
  - `Cosine`: Khuyên dùng tuyệt đối cho các bài toán NLP và Semantic Search (đo góc lệch giữa 2 câu).
  - `Dot Product` / `Euclidean`: Dành cho các bài toán phân loại đặc thù.

## 2. Payload Indexing (Bắt buộc tối ưu)
Mặc dù Payload (Metadata đính kèm vector) là JSON schema-less tự do, nhưng nếu không đánh Index, Qdrant sẽ phải full-scan toàn bộ DB để tìm kiếm, gây nghẽn cổ chai.
Bắt buộc phải tạo Payload Index cho các trường thường xuyên dùng để `filter`:
- **room_id** (kiểu `keyword`): Để giới hạn phạm vi tìm kiếm ngữ nghĩa chỉ nằm trong 1 cuộc họp cụ thể, tránh râu ông nọ cắm cằm bà kia.
- **timestamp** (kiểu `float`/`integer`): Để hỗ trợ AI lọc các đoạn hội thoại trong một khoảng thời gian nhất định (ví dụ: 10 phút gần nhất).

## 3. Implementation Example (Python Worker)
Đoạn code chuẩn bị Schema này nên được chạy 1 lần lúc startup của ứng dụng `warptalk-ai`.

```python
from qdrant_client import QdrantClient
from qdrant_client.http.models import Distance, VectorParams

client = QdrantClient(url="http://localhost:6333")

# 1. Tạo "Table" (Collection) với Schema cho Vector
client.recreate_collection(
    collection_name="meeting_transcripts",
    vectors_config=VectorParams(size=384, distance=Distance.COSINE),
)

# 2. Tạo "Index" cho Payload để lọc nhanh theo phòng họp
client.create_payload_index(
    collection_name="meeting_transcripts",
    field_name="room_id",
    field_schema="keyword",
)
```
