-- Chia sẻ biên bản: a link somebody outside the room can open, and a list of people invited to it.
--
-- WHY A LINK AT ALL
--     A biên bản exists to be circulated — to a client, to a partner, to the person who missed the
--     meeting. Today the only way out of the product is a download, so the file gets forwarded as
--     an attachment and the workspace loses sight of it entirely. A link keeps the record in one
--     place: it can be revoked, its mode can be narrowed, and the sender can see what they shared.
--
-- TWO MODES, AND THE SECOND ONE IS THE DEFAULT
--     INVITED_ONLY   — only the people named in meeting_minutes_share_grants may open it.
--     ANYONE_WITH_LINK — anybody holding the URL may open it, signed in or not.
--     A link is created INVITED_ONLY. Widening it is a deliberate act by the host, because a
--     public link cannot be un-sent: once the URL is out, revoking stops future reads but not the
--     copy somebody already took. The product says so at the moment of widening; this table only
--     records the decision and who made it.
--
-- WHY THE TOKEN IS ROTATED ON REVOKE RATHER THAN A ROW FLAG ALONE
--     Revoke means the URL in somebody's inbox stops working. Keeping the token and flipping a
--     flag would leave a live-looking URL that a bug, a cache or a future "unrevoke" could bring
--     back. Revoking clears the token to a fresh one; the old string is gone from the database.
--
-- WHY GRANTS ARE KEYED ON EMAIL
--     You invite the person, not their account: the partner you send minutes to may not have
--     signed up yet, and the room's own read gate (RoomReadAccess) already matches invitations on
--     email for exactly this reason. Lower-cased on write, because a person typing their own
--     address is not consistent about case and Nhi@ and nhi@ are one person.
--
-- WHAT THIS TABLE DOES NOT GRANT
--     Reading a shared minutes is reading THAT DOCUMENT. It is not membership of the room, and it
--     carries no access to the transcript, the recording or the room's other artifacts. The
--     services that own those keep their own gates; nothing here widens them.

CREATE TABLE IF NOT EXISTS translation_room.meeting_minutes_share_links (
    id uuid PRIMARY KEY DEFAULT uuidv7(),

    -- The room's minutes as a record, not one version of it: a link shared today must still open
    -- the document after the secretary issues a revision, the way a document link behaves
    -- everywhere else. Which version it resolves to is meeting_minutes.is_current.
    translation_room_id uuid NOT NULL
        REFERENCES translation_room.translation_rooms (id),

    -- External AuthService workspace id. No physical FK, matching every other cross-service
    -- reference in this schema.
    workspace_id uuid NOT NULL,

    -- URL-safe random, 32 bytes of entropy. Unguessable is the whole security of a public link,
    -- so this is never derived from the room id, the minutes number or a counter.
    token varchar(64) NOT NULL,

    -- INVITED_ONLY | ANYONE_WITH_LINK
    access_mode varchar(24) NOT NULL DEFAULT 'INVITED_ONLY',

    -- Whether a viewer may take the .docx/.pdf away, as opposed to reading it on screen. Off does
    -- not make a document unreadable-by-copy and the product must not claim otherwise; it is a
    -- statement of intent that removes the button.
    allow_download boolean NOT NULL DEFAULT true,

    -- Both nullable: a link with no expiry is the common case, and a live link has no revocation.
    expires_at timestamptz NULL,
    revoked_at timestamptz NULL,
    revoked_by uuid NULL,

    created_at timestamptz NOT NULL DEFAULT now(),
    created_by uuid NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid NULL
);

COMMENT ON TABLE translation_room.meeting_minutes_share_links IS
    'One sharing state per room''s biên bản: the link, its mode, and whether it is still live.';

COMMENT ON COLUMN translation_room.meeting_minutes_share_links.token IS
    'URL-safe random secret. Rotated on revoke so a revoked URL cannot be brought back.';

COMMENT ON COLUMN translation_room.meeting_minutes_share_links.access_mode IS
    'INVITED_ONLY (default) or ANYONE_WITH_LINK. Widening is a deliberate act by the host.';

-- One sharing state per room, the same way a document has one share dialog. Without this, two
-- links could disagree about the mode and revoking one would silently leave the other live.
CREATE UNIQUE INDEX IF NOT EXISTS meeting_minutes_share_links_room_idx
    ON translation_room.meeting_minutes_share_links (translation_room_id);

-- The lookup every public read does, and the guarantee that two rooms never share a token.
CREATE UNIQUE INDEX IF NOT EXISTS meeting_minutes_share_links_token_idx
    ON translation_room.meeting_minutes_share_links (token);

CREATE TABLE IF NOT EXISTS translation_room.meeting_minutes_share_grants (
    id uuid PRIMARY KEY DEFAULT uuidv7(),

    translation_room_id uuid NOT NULL
        REFERENCES translation_room.translation_rooms (id),

    -- Lower-cased on write. See the note above on why the person, not the account.
    email varchar(320) NOT NULL,

    -- Kept for the audit question every sharing feature eventually gets asked: who let this
    -- person in, and when.
    granted_by uuid NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE translation_room.meeting_minutes_share_grants IS
    'People invited by email to read a room''s biên bản. Reading the minutes; nothing else.';

CREATE UNIQUE INDEX IF NOT EXISTS meeting_minutes_share_grants_room_email_idx
    ON translation_room.meeting_minutes_share_grants (translation_room_id, email);
