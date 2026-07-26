-- Idempotent repair for workspace membership classification.
-- Run in the shared WarpTalk PostgreSQL database after deploying the
-- domain-derived membership classifier.

WITH resolved AS (
    SELECT
        member.id,
        member.workspace_id,
        member.user_id,
        member.membership_type AS old_membership_type,
        CASE
            WHEN member.user_id = workspace.owner_id THEN 'Internal'
            WHEN workspace.require_verified_domain_for_internal = FALSE THEN 'Internal'
            WHEN EXISTS (
                SELECT 1
                FROM workspace.workspace_verified_domains AS verified_domain
                WHERE verified_domain.workspace_id = member.workspace_id
                  AND lower(verified_domain.status) = 'verified'
                  AND verified_domain.verified_at IS NOT NULL
                  AND verified_domain.revoked_at IS NULL
                  AND (
                      lower(split_part(auth_user.email, '@', 2)) =
                          lower(trim(leading '@' from verified_domain.domain))
                      OR (
                          workspace.allow_subdomains = TRUE
                          AND lower(split_part(auth_user.email, '@', 2)) LIKE
                              '%.' || lower(trim(leading '@' from verified_domain.domain))
                      )
                  )
            ) THEN 'Internal'
            ELSE 'External'
        END AS new_membership_type
    FROM workspace.workspace_members AS member
    INNER JOIN workspace.workspaces AS workspace
        ON workspace.id = member.workspace_id
    INNER JOIN auth.users AS auth_user
        ON auth_user.id = member.user_id
    WHERE member.removed_at IS NULL
      AND workspace.deleted_at IS NULL
),
updated AS (
    UPDATE workspace.workspace_members AS member
    SET membership_type = resolved.new_membership_type
    FROM resolved
    WHERE member.id = resolved.id
      AND lower(member.membership_type) <> lower(resolved.new_membership_type)
    RETURNING
        member.id,
        member.workspace_id,
        member.user_id,
        resolved.old_membership_type,
        resolved.new_membership_type
)
SELECT *
FROM updated
ORDER BY workspace_id, user_id;
