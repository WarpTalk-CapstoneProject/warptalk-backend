# Implementation Plan: Restructuring Report 6 (Software User Guides) Section 3 Layout

Tái cấu trúc và sắp xếp lại bố cục của **Chương 3 (User Manual)** trong file **`My Drive/Report6/Report6_Software User Guides.docx`** nhằm đảm bảo sự thống nhất 100% giữa **Mục 3.1 Overview (Bảng Table 5)**, **Các mục con 3.2 đến 3.7**, và **Vị trí của các Hình minh họa (Figures 4–37)**.

## User Review Required

> [!IMPORTANT]
> **Đổi vị trí Workflow 2 và Workflow 3**:
> Theo yêu cầu của bạn, thứ tự luồng sử dụng trong **Bảng Table 5** và các **Section con** sẽ được đổi chỗ:
> - **Workflow 2 mới**: `Real-time Multilingual Meeting (Meeting Host / Participant)` (trước đây là Workflow 3).
> - **Workflow 3 mới**: `Voice Profiles: Listening Voice and Voice Cloning Consent (Workspace Member)` (trước đây là Workflow 2).

> [!NOTE]
> **Phân chia lại bố cục Section 3.2 và dời các Figures 8–13**:
> - Các hình `Figure 8` (Workspace Settings), `Figure 9` (Subscription Plan), `Figure 10` (Member Management), `Figure 11` (Documents), `Figure 12` (Knowledge Base), `Figure 13` (Dashboard) sẽ được rút khỏi mục Guest Entry (3.2.1) và chuyển về đúng các mục tương ứng của Workspace Owner / Admin và Workspace Billing.

---

## Proposed Changes

### `My Drive/Report6/Report6_Software User Guides.docx`

#### 1. Cập nhật Bảng Table 5 (User Manual Overview) trong Section 3.1
Sắp xếp lại bảng theo thứ tự mới (Đổi Workflow 2 & 3):

| Workflow # | Title | Responsible Actor | Main User Steps |
|---|---|---|---|
| **Workflow 1** | **Setup Workspace & Governance** | Workspace Owner / Workspace Admin | Register or log in; create workspace; configure workspace settings, timezone, allowed target languages, and AI privacy policies; invite members; manage member roles and meeting permissions; add custom terminology; upload documents and knowledge sources; review workspace dashboard. |
| **Workflow 2** *(Mới)* | **Real-time Multilingual Meeting** | Meeting Host / Participant | Create or join a meeting room; complete device check (microphone/speaker); admit waiting participants; enable real-time speech translation (Voice + Text or Text only); use meeting chat, live transcript, recording, and host management controls; end or leave meeting. |
| **Workflow 3** *(Mới)* | **Voice Profiles & Consent** | Workspace Member | Open Voice Profiles; select default listening voices; record or upload a voice sample to create a personal voice profile; review and manage voice cloning consent; delete voice profile when needed. |
| **Workflow 4** | **After the Meeting: Records & Artifacts** | Host / Authorized Workspace Member | Access completed meeting history; review meeting record, dual-language transcript, AI-generated summary, decisions, and action items; download artifacts; query WarpBot AI assistant about meeting or workspace knowledge. |
| **Workflow 5** | **Manage Workspace Billing & Subscription** | Workspace Owner | Access Workspace Billing; review credit balance, monthly burn rate, and usage trend; upgrade subscription plan or top up credits via Stripe checkout; inspect payment invoices and transaction history. |
| **Workflow 6** | **Platform Administration** | Platform Administrator | Access Admin Portal; monitor platform-level metrics and workspace health; review system audit logs; manage global glossary; inspect platform billing analytics; suspend or reactivate workspaces with required rationale. |

---

#### 2. Tái cấu trúc danh mục Section 3.2 đến Section 3.7 trong Tài liệu

Chuyển đổi thứ tự các chương con để khớp chính xác với Table 5:

- **Section 3.2: Setup Workspace & Governance (Workspace Owner / Workspace Admin)**
  - `3.2.1 Guest Access and Workspace Entry` *(Chỉ chứa Figure 4, 5, 6, 7)*
  - `3.2.2 Workspace Owner Setup and Operations`
    - `3.2.2.1 Create Workspace and Review Plan` *(Gồm Figure 13 Dashboard, Figure 14 Flow, Figure 15 Creation Option, Figure 16 Details)*
    - `3.2.2.2 Invite Members and Handle Join Requests` *(Gồm Figure 10 Member Management, Figure 17 Invite Member)*
    - `3.2.2.3 Manage Roles, Settings, Terminology and Knowledge` *(Gồm Figure 8 Settings, Figure 11 Documents, Figure 12 Knowledge Base, Figure 18 Governance)*
- **Section 3.3: Real-time Multilingual Meeting (Meeting Host / Participant)** *(Đã đổi từ 3.4 sang 3.3)*
  - `3.3.1 Create or Open a Meeting` *(Figure 22, 23)*
  - `3.3.2 Join Device Check and Use Translation` *(Figure 24)*
  - `3.3.3 Use Collaboration Tools` *(Figure 25)*
  - `3.3.4 End or Leave Meeting` *(Figure 26)*
- **Section 3.4: Voice Profiles: Listening Voice and Voice Cloning Consent (Workspace Member)** *(Đã đổi từ 3.3 sang 3.4)*
  - `3.4.1 Accept Workspace Access` *(Figure 19, 20)*
  - `3.4.2 Manage Personal Voice Profile` *(Figure 21)*
- **Section 3.5: After the Meeting: Transcript, Summary and Q&A (Host / Authorized Member)** *(Giữ nguyên vị trí)*
  - `3.5.1 View Meeting History` *(Figure 27, 28)*
  - `3.5.2 Review Transcript and Summary` *(Figure 29)*
  - `3.5.3 Manage Workspace Documents and Knowledge` *(Figure 30)*
  - `3.5.4 Ask WarpBot About Meeting Content` *(Figure 31)*
- **Section 3.6: Manage Workspace Billing & Subscription (Workspace Owner)** *(Phân tách riêng biệt)*
  - `3.6.1 Overview & Credit Tracking` *(Figure 9 Subscription, Figure 32 Billing Flow)*
  - `3.6.2 Top-Up & Invoices` *(Figure 33 Billing Screen)*
- **Section 3.7: Platform Administration (Platform Administrator)** *(Tách biệt khỏi Billing)*
  - `3.7.1 Operate Platform Administration` *(Figure 34, 35)*
  - `3.7.2 Manage Global Glossary and Admin Billing` *(Figure 36, 37)*

---

## Verification Plan

### Automated Verification
- Chạy script kiểm tra `python-docx` để đảm bảo file `.docx` mở thành công mà không phát sinh lỗi XML syntax hoặc lỗi hỏng file.
- Đảm bảo danh sách tiêu đề `Heading` và thứ tự `Figure` / `Table` được sắp xếp lại mượt mà.

### Manual Verification
- Mở file trên Google Drive để xác nhận không còn thông báo `CORRUPTED`.
- Kiểm tra trực quan xem Bảng Table 5 và thứ tự các chương 3.2 đến 3.7 đã khớp nhau hoàn toàn.
