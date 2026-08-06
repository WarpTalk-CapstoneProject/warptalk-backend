-- Migration: 20260806090200_quarantine_translation_stt_confidence
-- Ticket: WT-277, WT-278 (schema), WT-294 (this file)
-- Description:
--   The EXPAND half of the expand/contract pair opened by
--   scripts/migrations/051-06-08-2026-quarantine-translation-stt-confidence.sql, restated for the
--   logical database TranscriptService actually connects to.
--
--   WHY THIS FILE EXISTS — AND WHY IT IS THE URGENT ONE. 051 was written only into
--   warptalk-infrastructure/scripts/migrations/, which run-migrations.sh applies to the legacy
--   monolith database `warptalk`. TranscriptService connects to `warptalk_transcript`, which has
--   received NO migration since the logical-database extraction — this is the first file in its set.
--   051 reported "Migrations complete." against a database nothing reads.
--
--   The deployed TranscriptService (backend PR #106, WT-277/WT-278) maps ONLY the new column:
--   TranscriptDbContext.cs maps TranslationContent.SourceSttConfidence -> source_stt_confidence and
--   no longer maps `confidence` at all. TranscriptRedisConsumerService writes it on every persisted
--   translation. In `warptalk_transcript` that column does not exist, so the first translation a
--   meeting produces raises 42703 undefined_column on INSERT. Unlike the BillingService failures
--   this one has not fired yet only because no meeting has produced a translation since the deploy.
--
--   THIS FILE IS STILL THE EXPAND HALF. It does not rename and does not drop
--   transcript.translation_contents.confidence. Keeping the old column is what makes the file safe
--   to apply ahead of any TranscriptService build, including a rollback to a pre-#106 image, and it
--   is what the contract half in
--   warptalk-infrastructure/scripts/migrations/pending/052-06-08-2026-drop-translation-contents-confidence.sql
--   is waiting on. That file must stay uncollectable until the drop is deliberately promoted; note
--   that when it IS promoted it must be promoted HERE, into this directory, not into
--   scripts/migrations/ — the same mistake WT-294 is fixing.
--
--   COEXISTENCE. Between this migration and any older TranscriptService image, two writers exist and
--   they write different columns, so whichever column you read has a NULL hole in it. There is no
--   trigger and no sync job to close that hole, deliberately: nothing reads this value for
--   correctness (it reaches only TranscriptTranslationDto.Confidence and the transcript.proto
--   confidence field, both optional, and warptalk-web binds neither), and a NULL here already means
--   "unknown", which is exactly the meaning WT-277 established.
--
--   ASSUMPTION ABOUT EXISTING ROWS. The backfill copies values across as-is and does NOT reinterpret
--   them. The pre-WT-277 writer coalesced unknown to 1.0000, so a stored 1.0000 is ambiguous and must
--   be treated as UNRELIABLE in any confidence analysis. Only rows written after the WT-277 deploy
--   carry the NULL-means-unknown guarantee.
--
--   Idempotent and safe to re-run, including after the contract half has dropped `confidence`.

-- ─────────────────────────────────────────────────────────────
-- 1. WT-278 — add the correctly named column, alongside the old one
-- ─────────────────────────────────────────────────────────────
-- DECIMAL(5,4), matching transcript.translation_contents.confidence as declared in
-- 017-15-07-2026-translation-cluster-finalize.sql and the HasPrecision(5, 4) on
-- TranslationContent.SourceSttConfidence. Nullable, and it stays nullable: NULL is the WT-277
-- representation of "the producer reported no confidence", so a NOT NULL here would reintroduce the
-- exact defect this pair of tickets removes.
ALTER TABLE transcript.translation_contents
    ADD COLUMN IF NOT EXISTS source_stt_confidence DECIMAL(5,4);

-- One-time backfill for rows written before this migration. Guarded on the old column still
-- existing so this file remains re-runnable after the contract migration has dropped it.
-- The WHERE clause makes the copy idempotent and stops a re-run from overwriting a NULL that the
-- WT-277 backend deliberately wrote.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'transcript'
          AND table_name = 'translation_contents'
          AND column_name = 'confidence'
    ) THEN
        EXECUTE '
            UPDATE transcript.translation_contents
               SET source_stt_confidence = confidence
             WHERE source_stt_confidence IS NULL
               AND confidence IS NOT NULL';

        EXECUTE $c$
            COMMENT ON COLUMN transcript.translation_contents.confidence IS
                'DEPRECATED (WT-278), retained only for the expand/contract window opened by '
                'migration 051. Same value as source_stt_confidence and equally mislabelled: it is '
                'the SOURCE segment STT avg_logprob, never a translation quality score. No deployed '
                'TranscriptService writes it any more. Do not read it, do not add new writers. '
                'Dropped by the contract half once that is deliberately promoted.'
        $c$;
    END IF;
END $$;

-- ─────────────────────────────────────────────────────────────
-- 2. WT-277 — NULL must remain expressible on every confidence column
-- ─────────────────────────────────────────────────────────────
-- Both are already nullable, so these are no-ops today. They are here so that a future migration
-- adding NOT NULL has to consciously undo an explicit statement, and they are safe against any
-- deployed backend: relaxing a constraint cannot break a writer.
ALTER TABLE transcript.transcript_segments
    ALTER COLUMN confidence DROP NOT NULL;

ALTER TABLE transcript.translation_contents
    ALTER COLUMN source_stt_confidence DROP NOT NULL;

-- Same pin for the deprecated column, guarded so this file still re-runs after the contract half has
-- dropped it.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'transcript'
          AND table_name = 'translation_contents'
          AND column_name = 'confidence'
    ) THEN
        EXECUTE 'ALTER TABLE transcript.translation_contents
                     ALTER COLUMN confidence DROP NOT NULL';
    END IF;
END $$;

-- ─────────────────────────────────────────────────────────────
-- 3. Say in the schema what these columns are
-- ─────────────────────────────────────────────────────────────
COMMENT ON COLUMN transcript.transcript_segments.confidence IS
    'STT model confidence for this segment (an avg_logprob, so <= 0). NULL = the producer reported '
    'no confidence (no token logprobs on the event, an unparsable field, or the -1.0 sentinel) — '
    'WT-277: never coalesce this to a number on write. Rows written before the WT-277 backend '
    'deploy whose value is exactly 1.0000 are unreliable: the old writer used 1.0 as its '
    'missing-value default.';

COMMENT ON COLUMN transcript.translation_contents.source_stt_confidence IS
    'WT-278: the STT confidence of the SOURCE segment this translation was derived from — a '
    'measurement of the audio, NOT of the translation. Supersedes the "confidence" column, which '
    'this migration keeps alive only until the contract half. The translator produces no quality '
    'score; do not surface this as translation quality in any API, export or UI. NULL = the source '
    'segment carried no usable confidence, OR the row was written by a pre-WT-277 backend that only '
    'knew the old column. A real translation quality signal (back-translation, COMET-style scoring) '
    'must get its own new column, not reuse this one.';
