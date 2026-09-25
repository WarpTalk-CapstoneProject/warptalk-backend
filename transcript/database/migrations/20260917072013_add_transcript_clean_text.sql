-- Migration: 20260917072013_add_transcript_clean_text
-- Ticket: WT-716
-- Description:
--
-- "Clean transcript" — filler words (ừm, あの, uh) and stutters removed, and the result read as
-- whole sentences. Two tiers, stored side by side with the raw record and NEVER instead of it:
--
--   Tier 1, per segment, deterministic. stt_worker publishes clean_text/clean_flags on the same
--   stt:results message as the raw text, so the cleaned line lands on the segment row it belongs
--   to. original_text is untouched: summary citations, transcript corrections, exports and the
--   Verbatim view all keep reading the words that were actually recognised.
--
--   Tier 2, per sentence, LLM. Produced after segments are final, published on transcript:clean,
--   and mapped back onto raw segment ids (segment_ids) — a sentence is a VIEW over segments, not a
--   replacement of them, which is why it is its own table rather than a rewrite of the rows.
--
-- No backfill. NULL clean_text means "never cleaned" (every row written before this migration, and
-- any segment from an older stt_worker); readers fall back to original_text. An EMPTY clean_text is
-- different and deliberate: the segment was filler only.
--
-- Workspace isolation mirrors transcript_segments exactly: no workspace_id of its own, reached only
-- through transcript_id → transcripts.workspace_id, and read only through the same transcript read
-- gate as the segments.

ALTER TABLE transcript.transcript_segments
    ADD COLUMN IF NOT EXISTS clean_text  text   NULL,
    ADD COLUMN IF NOT EXISTS clean_flags text[] NULL;

COMMENT ON COLUMN transcript.transcript_segments.clean_text IS
    'WT-716 tier 1: original_text with fillers/stutters removed by stt_worker. NULL = not cleaned (read original_text); empty string = the segment was filler only.';
COMMENT ON COLUMN transcript.transcript_segments.clean_flags IS
    'WT-716 tier 1: subset of filler_only, fillers_removed, stutter_removed, escalate. NULL when clean_text is NULL.';

CREATE TABLE IF NOT EXISTS transcript.transcript_clean_sentences (
    -- The producer's sentence_id, not a server-minted id: a later revision of the same sentence has
    -- to find this row to replace it.
    id                      uuid PRIMARY KEY,
    transcript_id           uuid NOT NULL
        CONSTRAINT transcript_clean_sentences_transcript_id_fkey
        REFERENCES transcript.transcripts (id),
    speaker_participant_id  uuid NULL,
    -- Deliberately NOT a foreign key (an array cannot be one anyway): tier 2 can outrun the
    -- persistence of the very segments it covers, and a sentence must not be refused for it.
    segment_ids             uuid[] NOT NULL
        CONSTRAINT transcript_clean_sentences_segment_ids_not_empty CHECK (cardinality(segment_ids) > 0),
    clean_text              text NOT NULL,
    language                varchar(15) NOT NULL,
    flags                   text[] NOT NULL DEFAULT '{}',
    source                  varchar(20) NOT NULL,
    revision                int NOT NULL
        CONSTRAINT transcript_clean_sentences_revision_non_negative CHECK (revision >= 0),
    -- Earliest start_time_ms among the covered segments that were already stored when this
    -- revision was written. NULL when none were (the race above). Conversation order on read is
    -- taken from the segments' sequence_order, this is only the fallback.
    start_time_ms           int NULL,
    -- The producer's timestamp_ms for this revision.
    produced_at             timestamptz NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE transcript.transcript_clean_sentences IS
    'WT-716 tier 2: whole cleaned sentences over raw transcript_segments. Upserted by (id, revision): a higher revision replaces, a lower or equal one is ignored.';
COMMENT ON COLUMN transcript.transcript_clean_sentences.speaker_participant_id IS
    'External TranslationRoomService participant id. No physical FK. NULL for the system speaker.';
COMMENT ON COLUMN transcript.transcript_clean_sentences.segment_ids IS
    'Raw transcript_segments ids this sentence covers, in order. No FK: may name segments not persisted yet.';
COMMENT ON COLUMN transcript.transcript_clean_sentences.flags IS
    'Subset of self_repair, fallback_raw, escalate.';
COMMENT ON COLUMN transcript.transcript_clean_sentences.source IS
    'llm | prepass (unknown when the producer did not say).';

-- Every read is "all sentences of one transcript".
CREATE INDEX IF NOT EXISTS transcript_clean_sentences_transcript_id_idx
    ON transcript.transcript_clean_sentences (transcript_id);
