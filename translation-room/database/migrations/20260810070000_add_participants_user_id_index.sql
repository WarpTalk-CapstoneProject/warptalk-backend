-- WT-333: the personal-timeline read asks "which rooms is this user in", which is a lookup by
-- user_id alone. The only index covering this table's user column is
-- (translation_room_id, user_id) — user_id is the trailing column, so Postgres cannot use it to
-- satisfy a predicate that does not also constrain translation_room_id, and the query degrades to
-- a sequential scan that grows with every participant row in the tenant.
--
-- CONCURRENTLY is deliberately NOT used: these migration files run inside the deployment's
-- transaction, and CREATE INDEX CONCURRENTLY cannot. The table is small enough today that the
-- brief write lock is the cheaper trade; if that stops being true, this statement has to move to
-- its own out-of-band step rather than losing the transaction around the rest of a release.
CREATE INDEX IF NOT EXISTS translation_room_participants_user_id_idx
    ON translation_room.translation_room_participants (user_id);
