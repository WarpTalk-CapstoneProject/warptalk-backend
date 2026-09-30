-- Migration: 20260918025049_add_segment_translation_link_is_stale
-- Ticket: WT-704
-- Description:
--
-- When a host corrects the spoken text of a transcript line (STT correction), every translation
-- already linked to that line was produced from the old sentence and no longer says what the line
-- now says. The correction flow retranslates only into the languages the meeting still allows;
-- every other language keeps its old translation and has to be shown as outdated rather than as
-- if it were current.
--
-- The flag lives on the link, not on transcript.translation_contents: a content row is
-- deduplicated per (workspace, text_hash, target_language) and shared by every segment that
-- produced the same translated string, so "outdated" is a property of one segment's use of it.
--
-- It is an explicit column rather than something inferred from created_at: when a retranslation
-- produces exactly the same text, the consumer finds the link already in place and inserts
-- nothing, so no timestamp would ever move and the line would read as outdated forever.
--
-- Forward-only and idempotent. Existing rows default to false — nothing is stale until a
-- correction says so.

ALTER TABLE transcript.segment_translation_links
    ADD COLUMN IF NOT EXISTS is_stale boolean NOT NULL DEFAULT false;

COMMENT ON COLUMN transcript.segment_translation_links.is_stale IS
    'WT-704: true when an STT correction changed the source sentence after this translation was produced. Cleared when the retranslation arrives (same content re-linked) or a new translation is linked for the segment/language. Clients show a stale translation as outdated.';
