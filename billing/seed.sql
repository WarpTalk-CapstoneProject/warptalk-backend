INSERT INTO billing."UsageQuotas" ("Id", "WorkspaceId", "PlanId", "TotalAllocatedMinutes", "ConsumedMinutes", "CycleStartDate", "CycleEndDate", "CreatedAt", "UpdatedAt") 
VALUES ('99999999-9999-9999-9999-999999999999', '77777777-7777-7777-7777-777777777777', '22222222-2222-2222-2222-222222222222', 500, 0, '2026-01-01', '2026-12-31', now(), now()) 
ON CONFLICT DO NOTHING;
