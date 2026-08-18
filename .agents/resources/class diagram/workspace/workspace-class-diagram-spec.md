# Class Diagram Specification - Workspace Module

Key classes of the Workspace module are described in the Class Specification table below.

| Class | Field / Method | Description |
| :--- | :--- | :--- |
| `Workspace` | `Id, Name, Slug, OwnerId, AvatarUrl, AllowExternalCollaboration, RequireVerifiedDomainForInternal, Settings, IsActive, CreatedAt` | Organizational tenant boundary entity for multi-tenancy; `Slug` is unique and collision-resolved for workspace routing. |
| `WorkspaceMember` | `Id, WorkspaceId, UserId, RoleId, MembershipType, Status, CanCreateMeetings, JoinedAt` | Workspace membership entity; distinguishes internal vs external members, role assignments (`Owner`, `Admin`, `Member`), and active vs suspended states. |
| `WorkspaceInvitation` | `Id, WorkspaceId, Email, RoleId, MembershipType, MatchedDomainId, InvitedBy, TokenHash, Status, DeliveryStatus, SentCount, RequestedBy, ReviewedBy` | Tracks pending workspace invitations and join requests; enforces domain policy checks, token validation, and admin approval workflows. |
| `WorkspaceVerifiedDomain` | `Id, WorkspaceId, DomainName, Status, VerificationMethod, VerificationToken, VerifiedAt, VerifiedBy` | Configured corporate domain entity (e.g. `@fpt.edu.vn`) used to auto-verify or restrict workspace membership join requests. |
| `WorkspaceDocument` | `Id, WorkspaceId, StorageKey, FileName, FileExtension, FileSizeBytes, IngestionStatus, ConfidentialityLevel` | Represents stored workspace documents; tracks encrypted storage keys, ingestion states for AI RAG, and confidentiality classifications. |
| `WorkspaceDocumentAccessPolicy` | `Id, DocumentId, WorkspaceId, TargetRole, SubjectType, SubjectId, Permission, Effect` | Per-document policy rule defining fine-grained allow or deny access permissions for subjects (users/roles/groups). |
| `WorkspaceDocumentAudit` | `Id, DocumentId, WorkspaceId, ActorId, Action, ActionAt, Metadata, IpAddress, UserAgent` | Audit record tracking document operations (upload, read, download, delete) with actor metadata. |
| `WorkspaceEntitlementSnapshot` | `WorkspaceId, Entitlements, PlanSlug, HasActiveSubscription, ResolvedAt, LastEventId` | Read-model snapshot projecting workspace feature quotas and entitlements calculated from asynchronous billing events. |
| `WorkspaceAdminAction` | `Id, WorkspaceId, ActionType, Reason, AdminUserId, DetailsJson, PerformedAt, CorrelationId` | Audit log record capturing administrative actions executed at workspace or platform level. |
| `WorkspacesController` | `CreateWorkspace(...), GetWorkspaces(...), GetWorkspaceById(...), SelectWorkspace(...)` | REST controller managing workspace creation, tenant querying, details retrieval, and workspace switching context. |
| `WorkspaceMembersController` | `GetMembers(...), UpdateMemberRole(...), RemoveMember(...)` | REST controller managing workspace membership lists, role updates (`Owner`, `Admin`, `Member`), and member removals. |
| `WorkspaceInvitationsController` | `InviteMember(...), AcceptInvitation(...), CreateJoinRequest(...)` | REST controller handling member invitation issuance, token acceptance, and workspace join request submissions. |
| `WorkspaceDocumentsController` | `UploadDocument(...), ListDocuments(...), DownloadDocument(...)` | REST controller handling document uploads, document list queries, and encrypted stream downloads. |
| `AdminWorkspacesController` | `GetAllWorkspaces(...), UpdateWorkspaceStatus(...)` | System Admin REST controller providing system-wide workspace telemetry and lifecycle management (suspend/reactivate). |
| `WorkspaceGrpcService` | `ValidateWorkspaceAccess(...)` | High-performance gRPC endpoint enabling cross-service validation of user workspace access and role permissions. |
| `WorkspaceService` | `CreateWorkspaceAsync(...), GetWorkspacesAsync(...), GetWorkspaceByIdAsync(...)` | Core application service enforcing multi-tenancy invariants, single-owner rules, and workspace metadata management. |
| `WorkspaceMemberService` | `GetMembersAsync(...), UpdateMemberRoleAsync(...), RemoveMemberAsync(...)` | Application service governing workspace role assignments, membership status transitions, and soft-deletion. |
| `WorkspaceInvitationService` | `InviteMemberAsync(...), AcceptInvitationAsync(...)` | Application service processing invitation token validations, email dispatches, and join eligibility checks. |
| `WorkspaceDocumentService` | `UploadDocumentAsync(...), ListDocumentsAsync(...)` | Application service managing document ingestion, metadata extraction, encrypted storage, and RAG trigger events. |
| `WorkspaceKnowledgeService` | `IndexDocumentChunksAsync(...), SearchKnowledgeAsync(...)` | Application service driving vector indexing of document chunks into Qdrant DB and semantic search queries. |
| `AdminWorkspaceService` | `AdminGetWorkspacesAsync(...), AdminSetWorkspaceStatusAsync(...)` | System Admin application service performing platform-wide workspace audits and lifecycle status updates. |
| `VerifiedDomainService` | `AddDomainAsync(...), VerifyDomainDnsAsync(...)` | Application service managing organization domain verification (`@company.com`) via DNS verification. |
| `WorkspaceOutboxDispatcher` | `DispatchPendingEventsAsync(...)` | Background worker processing transactional outbox messages to publish workspace domain events to other microservices. |
| `WorkspaceDbContext` | `Workspaces, WorkspaceMembers, WorkspaceInvitations, WorkspaceDocuments, WorkspaceDocumentAccessPolicies, WorkspaceVerifiedDomains, WorkspaceAdminActions` | Entity Framework Core DbContext managing workspace persistence across multi-tenant relational tables. |
| `UnitOfWork` | `SaveChangesAsync(), BeginTransactionAsync(), CommitTransactionAsync()` | Manages transactional consistency for multi-entity workspace operations. |
| `WorkspaceRepository` | `GetByIdAsync(...), GetWorkspacesForUserAsync(...), AddAsync(...)` | Persistence repository for retrieving workspace aggregate roots and user workspace lists. |
| `WorkspaceMemberRepository` | `GetActiveMembersByWorkspaceAsync(...), AddAsync(...)` | Persistence repository managing workspace membership records and active user roles. |
| `WorkspaceInvitationRepository` | `GetByTokenHashAsync(...), AddAsync(...)` | Persistence repository storing and validating invitation tokens and join requests. |
| `WorkspaceDocumentRepository` | `GetPagedDocumentsAsync(...), AddAsync(...)` | Persistence repository querying workspace document metadata and ingestion statuses. |
| `WorkspaceVerifiedDomainRepository` | `GetByWorkspaceIdAsync(...), AddAsync(...)` | Persistence repository managing corporate domain verification records. |
| `LocalEncryptedWorkspaceDocumentStorage` | `SaveDocumentContentAsync(...), ReadDocumentContentAsync(...)` | Storage infrastructure component handling encrypted file blob writes and stream reads. |
| `QdrantVectorStoreAdapter` | `UpsertVectorsAsync(...), QuerySimilarVectorsAsync(...)` | Infrastructure adapter interfacing with Qdrant Vector Database for vector upserts and Cosine similarity queries. |
