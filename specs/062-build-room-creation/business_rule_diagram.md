# Translation Room: Participant Join Flow Business Rules

This diagram illustrates the state machine and business rules (`BR-002`, `BR-004`, `BR-005`) applied when a user attempts to join a Translation Room.

```mermaid
stateDiagram-v2
    [*] --> JoinRequest : User attempts to join with Room Code
    
    state JoinRequest {
        direction TB
        
        IsHost : Is user the Host?
        CheckPreviousState : Check existing Participant record
        CheckApproval : translationRoom.Settings.requires_approval?
        
        JoinRequest --> IsHost
        
        IsHost --> CONNECTED : Yes (Host auto-connects - BR-002)
        IsHost --> CheckPreviousState : No (Member)
        
        CheckPreviousState --> CheckApproval : No record (New Participant)
        CheckPreviousState --> KICKED_Check : Existing record
        
        KICKED_Check : Is Status == KICKED?
        KICKED_Check --> Blocked : Yes (BR-005 - 403 Forbidden)
        KICKED_Check --> CheckApproval : No (REJECTED / DISCONNECTED / LEFT)
        
        CheckApproval --> WAITING : Yes (requires_approval = true)
        CheckApproval --> CONNECTED : No (requires_approval = false)
    }
    
    Blocked --> [*]
    WAITING --> [*]
    CONNECTED --> [*]
    
    note right of CheckApproval
        BR-004:
        The requires_approval setting determines 
        if non-host users (new or returning)
        go to WAITING or CONNECTED.
    end note
```
