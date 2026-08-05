# WarpTalk — Capstone Project Register
> **Source**: [Google Doc](https://docs.google.com/document/d/1cFjnBBt18s6HvX542cSLVi5oJQ6L8Oud82j2DwklE30/edit)
> **Supervisor**: Thân Thị Ngọc Vân (VanTTN2@fe.edu.vn)
> **Duration**: 01/01/2026 → 30/04/2026

---

## Team

| Name | Student Code | Email | Role |
|------|-------------|-------|------|
| Huỳnh Mạnh | SS160137 | manhhss160137@fpt.edu.vn | Member |
| Trần Mạnh Tuấn | SE180360 | tuantmse180360@fpt.edu.vn | Member |
| **Huỳnh Thái Tú** | **SE183307** | tuhtse183307@fpt.edu.vn | **Leader** |
| Huỳnh Ngọc Kỳ | SE182995 | kyhnse182995@fpt.edu.vn | Member |

---

## Project Name

- **English**: WarpTalk — An AI Speech Translation Platform for Real-Time Multilingual Communication with Voice Cloning
- **Vietnamese**: WarpTalk — Nền tảng AI Dịch Giọng Nói Tự Nhiên Đa Ngôn Ngữ Theo Thời Gian Thực với Công Nghệ Sao Chép Giọng Nói

---

## Context

Language barriers in remote/global teams cause inefficiencies and miscommunication. Existing tools introduce delays and don't preserve the speaker's tone, emotion, or vocal identity. WarpTalk solves this with **real-time voice-to-voice translation** + **Voice Cloning**, acting as intelligent middleware for Zoom/Google Meet/Teams.

---

## Core Features

### 1. Real-time Speech Translation
Capture voice → STT → Translate → TTS with voice cloning → Output in listener's language.

### 2. Voice Cloning
Preserves speaker's original intonation and emotions during translated output.

### 3. Meeting Transcript Display
Real-time transcripts (original + translated) with interactive correction — edits sync & trigger re-translation.

### 4. AI Meeting Assistant
Auto-summarizes key points, decisions, suggestions, and action items during the meeting.

### 5. Meeting Feedback & Insights
Post-meeting analysis of communication effectiveness.

### 6. Virtual Audio Device Integration (One-to-Many / B2B)
OS-level Virtual Microphone injects translated audio into Zoom/Meet — zero setup for external clients.

### 7. Multi-Language Collaborative Mode (Any-to-Any)
Parallel audio routing: each participant hears a dedicated, voice-cloned stream in their chosen language.

---

## User Roles & Functional Requirements

| Role | Capabilities |
|------|-------------|
| **System Admin** (web) | Manage AI services, workspaces, global stats |
| **Workspace Admin** (web) | Manage users/roles, Enterprise Glossary, subscriptions |
| **Registered User** (web) | Profile, meeting history, transcript downloads, feedback |
| **Meeting Host** (web + desktop) | Create/schedule meetings, configure languages, manage lifecycle |
| **Meeting Participant** (desktop) | Join via Meeting ID, select languages, speak/listen in real-time, correct transcripts |
| **External Systems** | Zoom/Meet receives processed audio via Virtual Audio Driver |

---

## Non-Functional Requirements

| Category | Requirements |
|----------|-------------|
| **Performance** | Dashboard ≤3s load; Translation latency ≤2s per semantic chunk; AI feedback ≤5s; Summary ≤15s post-meeting |
| **Reliability** | ≥99% uptime; Graceful degradation to text-only mode; Reliable transcript storage |
| **Scalability** | Redis Streams for backpressure; Modular scaling of API Gateway + GPU AI Workers |
| **Security** | HTTPS + WSS; RBAC + JWT; Account lockout after 5 failed logins |
| **Usability** | Dynamic transcripts with inline correction; Status indicators; Cross-platform Electron app |
| **Maintainability** | Clean Architecture; Swagger docs; Load testing; E2E pipeline testing |

---

## Subscription Plans (Host-pays Model via PayOS)

| Tier | Price | Credits | Key Features |
|------|-------|---------|-------------|
| **Free** | 0 VND | 30 one-time | Standard TTS, join as participant, text-only after exhaustion |
| **Pro** | 500K VND/mo | 300/mo | Voice Cloning, bi-directional (2 languages), AI summaries |
| **Premium** | 1.5M VND/mo | 1,000/mo | Any-to-Any multilingual, high-fidelity cloning, AI assistant, workspace dashboard |
| **Enterprise** | Custom (~10M VND/mo) | 5,000+/mo | Dedicated GPU, Enterprise Glossary, multi-session, API integrations |

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| **Backend Gateway** | .NET Core, SignalR/WebSockets, Redis Streams |
| **Database** | PostgreSQL + Qdrant (vector DB for voice embeddings & glossary) |
| **AI Workers (Python/GPU)** | Fast-Whisper (STT), LLM (translation), XTTS v2 / Cartesia / ElevenLabs (TTS/Voice Cloning) |
| **Web App** | ReactJS + NextJS (Landing, Admin, Workspace Portal) |
| **Desktop App** | ElectronJS + WebRTC/Web Audio API + Virtual Audio Drivers |

---

## Deliverables

1. **WarpTalk Core & AI Engine** — .NET/Python backend pipeline (audio routing, STT, LLM Translation, Voice Cloning TTS)
2. **WarpTalk Meeting Client** — Cross-platform Electron desktop app
3. **WarpTalk Management Portal** — ReactJS web dashboard

---

## Task Packages

1. **Foundation & Architecture** — Cloud infra, PostgreSQL/Qdrant, .NET Gateway, Redis Streams
2. **AI Pipeline Integration** — STT, Machine Translation, Voice Cloning, AI Assistant workers
3. **Real-time Audio Streaming** — SignalR/WebSocket, Web Audio API, Virtual Audio Driver routing
4. **Web & UI/UX** — ReactJS portal (admin, workspace, scheduling, transcripts)
5. **Desktop App & Interactive Transcript** — Meeting Client UI, multi-language selection, on-the-fly correction
6. **Testing, Optimization & Deployment** — E2E testing, load testing, latency optimization
7. **Documentation** — System Architecture, API Specs (Swagger), User Manual, Installation Guides
