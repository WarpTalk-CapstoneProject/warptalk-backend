# WarpTalk Backend - Task & Progress Tracking

## ✅ Completed Progress (Phase 0 & 1)

**Architecture & Planning:**
- [x] Defined 5-repo microservice architecture
- [x] Designed database schema (33 tables, 5 schemas)
- [x] Implemented `init-db.sql` initialization script
- [x] Handled architecture audit & gap analysis

**AuthService Development:**
- [x] Scaffolded `AuthService` with Clean Architecture (Domain, Application, Infrastructure, API)
- [x] EF Core scaffolded entities from PostgreSQL `auth` schema
- [x] Configured Unit of Work & Generic Repository pattern
- [x] Implemented DTOs & Business Logic (`AuthService`)
- [x] Implemented `PasswordHasher` & `JwtTokenGenerator`
- [x] Implemented `GoogleAuthService` (Google OAuth token verification)
- [x] Configured Dependency Injection in `Program.cs`
- [x] Built `AuthController` (Register, Login, GoogleLogin, Refresh, Logout, Profile, ChangePassword)
- [x] Build verified with 0 errors

---

## 🚀 Next Steps (Upcoming Services Scaffold & Features)

### 1. API Gateway (`gateway/`)
- [x] Create `Ocelot` or `YARP` API Gateway project
- [x] Configure routing to `AuthService`
- [x] Setup global JWT authentication & rate limiting
- [x] Dockerize the Gateway

### 2. Workspace & Meeting Service (`meeting/`)
- [ ] Scaffold `MeetingService` (Clean Architecture)
- [ ] EF Console scaffold `meeting` and `workspace` schemas
- [ ] Implement Workspace CRUD (Create, Update, Invite members, RBAC roles)
- [ ] Implement Meeting scheduling logic (Create, Update, Cancel meetings)
- [x] Setup SignalR Hub for real-time meeting state synchronization (MeetingHub in Gateway)
- [ ] Integrate WebRTC signaling logic

### 3. Transcript Service (`transcript/`)
- [ ] Scaffold `TranscriptService` (Clean Architecture)
- [ ] EF Console scaffold `transcript` schema
- [ ] Setup logic for real-time speech-to-text (e.g., connecting to external AI service)
- [ ] Implement saving meeting transcripts
- [ ] Implement meeting summarization jobs

### 4. Notification Service (`notification/`)
- [ ] Scaffold `NotificationService`
- [ ] Setup persistent notification logic (save to `notification` schema)
- [x] Setup SignalR for real-time push notifications (NotificationHub in Gateway)
- [ ] Implement Email notifications (Meeting invites, reminders)

### 5. Subscription & Payment Service (`subscription/`)
- [ ] Scaffold `SubscriptionService`
- [ ] Link `billing` schema
- [ ] Implement Razorpay/Stripe webhooks for capturing payment
- [ ] Handle subscription lifecycle (active, expired, canceled)

### 6. Integration & Testing
- [ ] Write Unit Tests for all core layers
- [ ] Create `docker-compose.yml` for local multi-container orchestrator (PostgreSQL, Redis, RabbitMQ, Services)
- [ ] Setup gRPC or RabbitMQ for inter-service communication (e.g., Auth communicating with Workspaces)
