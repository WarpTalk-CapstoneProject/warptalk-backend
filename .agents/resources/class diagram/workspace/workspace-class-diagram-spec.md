# Class Diagram Specification - Workspace Module

Key classes of the Workspace module are described in the Class Specification table below.

| Class | Field / Method | Description |
| :--- | :--- | :--- |
| `Workspace` | `Id, Name, Slug, Status, Settings` | Organizational tenant boundary for multi-tenancy; `Slug` is unique and collision-resolved for workspace routing. |
| `WorkspaceMember` | `Id, WorkspaceId, UserId, RoleId, MembershipType, Status` | Workspace membership assignment; distinguishes internal vs external members and tracks active vs suspended membership. |
| `WorkspaceInvitation` | `Id, WorkspaceId, Email, RoleId, TokenHash, Status, AllowedFinalMembershipTypes, RequiresPolicyAction` | Tracks pending workspace invitations and join requests; enforces domain policy checks and allowed membership roles upon approval. |
| `WorkspaceDocument` | `Id, WorkspaceId, UploadedBy, ConfidentialityLevel, StorageKey` | Represents stored workspace documents; linked with security classification levels and encrypted storage paths. |
| `WorkspaceVerifiedDomain` | `Id, WorkspaceId, Domain` | Configured corporate domains (e.g., `@fpt.edu.vn`) used to auto-verify or restrict join requests for workspace membership. |
| `WorkspacesController` | `createWorkspace(...), updateMemberRole(...), transferOwnership(...), updateWorkspaceSettings(...)` | API boundary controller for workspace creation, role updates, ownership transfer, and auto-save settings configuration. |
| `WorkspaceInvitationsController` | `inviteMember(...), acceptInvitation(...), createJoinRequest(...), approveJoinRequest(...)` | API boundary controller managing invitation dispatches, token acceptances, join request submissions, and policy-driven approvals. |
| `WorkspaceDocumentsController` | `uploadDocument(...), downloadDocument(...), deleteDocument(...)` | API boundary controller handling document uploads, encrypted stream downloads, and document deletion. |
| `WorkspaceService` | `createWorkspaceAsync(...), updateMemberRoleAsync(...), transferOwnershipAsync(...)` | Core application service enforcing single-owner invariants, role governance, and workspace lifecycle transitions. |
| `WorkspaceInvitationService` | `inviteMemberAsync(...), acceptInvitationAsync(...), approveJoinRequestAsync(...)` | Application service evaluating eligibility policies, token validations, and join request approval workflows. |
| `WorkspaceDocumentService` | `uploadDocumentAsync(...), getDocumentDownloadStreamAsync(...), deleteDocumentAsync(...)` | Application service handling document security scanning, storage key generation, and file stream retrieval. |
| `DocumentSecurityGuardrailConsumer` | `consumeDocumentUploaded(...), applySecurityDecision(...)` | Asynchronous background worker inspecting uploaded documents for security compliance, virus checks, and confidentiality tagging. |
| `EmbeddingIndexPublisher` | `publishEmbeddingIndexRequestAsync(...)` | Background integration service extracting document text for vector indexing in RAG AI pipelines. |
| `EncryptedStorage` | `store(...), getDecryptedStreamAsync(...)` | Infrastructure component encrypting file payloads before persistent storage and decrypting stream reads. |
