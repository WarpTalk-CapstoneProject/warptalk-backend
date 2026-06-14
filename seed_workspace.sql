INSERT INTO workspace.workspaces (id, name, slug, allow_external_collaboration, require_verified_domain_for_internal, allow_subdomains, owner_id) 
VALUES ('019ec641-97a7-78c9-8f18-000000000000', 'WarpTalk HQ', 'warptalk-hq', true, false, true, '019ec641-9776-7d50-b2b9-9edb93a46d22')
ON CONFLICT (slug) DO NOTHING;

INSERT INTO workspace.workspace_members (id, workspace_id, user_id, role_id, membership_type, status)
VALUES 
(gen_random_uuid(), '019ec641-97a7-78c9-8f18-000000000000', '019ea677-6c84-7d7b-9f48-738b3cde41a9', '99bf57ba-9d3c-471b-a5ae-94901a0c81b4', 'internal', 'active'), -- admin
(gen_random_uuid(), '019ec641-97a7-78c9-8f18-000000000000', '019ec641-9776-7d50-b2b9-9edb93a46d22', '99bf57ba-9d3c-471b-a5ae-94901a0c81b4', 'internal', 'active'), -- owner
(gen_random_uuid(), '019ec641-97a7-78c9-8f18-000000000000', '019ec641-97a7-78c9-8f18-de4e16e98ace', '95beb6bb-a255-4958-891f-68fa540ebe3d', 'internal', 'active')  -- member
ON CONFLICT (workspace_id, user_id) DO NOTHING;
