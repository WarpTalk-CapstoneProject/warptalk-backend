-- Seeds the global glossary for the TRANSCRIPT service database.
--
-- This lives here, not in warptalk-infrastructure/scripts/migrations/, because that folder
-- targets only the legacy `warptalk` database and reaches no service — the migration-coverage
-- contract rejected the first attempt for exactly that reason. `transcript.global_glossary_terms`
-- is owned by this service, so the seed belongs beside the service that reads it.
--
-- Seed the global glossary with the proper nouns this product keeps mistranscribing.
--
-- A production rehearsal produced "Codex" as "cô đích" and "voice clone" as "Voiclon".
-- These are not translation failures — they are recognition failures. The STT model has no
-- idea these words exist, so it reaches for whatever Vietnamese phonemes fit, and the
-- translator then faithfully translates the nonsense.
--
-- Both halves of the pipeline already read this table. GlossaryStartedEventConsumer publishes
-- published rows to `translationRoom:{roomId}:stt_keywords` (contextual biasing, so the model
-- expects these strings) and to `translationRoom:{roomId}:mt_glossary` (so translation keeps
-- them verbatim). Nothing new is wired here; the table was simply empty of the words this
-- team actually says.
--
-- CONVENTION: preferred_translation equal to term (case-insensitively) means "keep verbatim,
-- do not translate" — see translation_worker.translator._build_glossary_block. Every row here
-- is a proper noun, so every row uses it.
--
-- source_language and target_language are NULL throughout: a product name is the same string
-- whichever direction the meeting runs in.
--
-- priority orders the list when it is trimmed to fit the prompt budget, highest first. The
-- ranking below is deliberate: what this product is called and what it is built on beats
-- general industry vocabulary, because those are the words a defence panel will hear and the
-- ones with no Vietnamese equivalent to fall back on.
--
-- status is 'published' because a draft row is invisible to the consumer. The service
-- requires a definition before publishing (GlobalGlossaryService §6), which is why each row
-- carries one rather than being seeded bare.
--
-- Idempotent: the unique dedup index is on lower(term) plus the COALESCEd language/domain
-- columns, so ON CONFLICT DO NOTHING makes a re-run a no-op rather than a duplicate-key
-- failure.

-- DELIBERATELY NOT SEEDED HERE: the names of real people.
--
-- An earlier draft of this migration listed the project team and their supervisor. That was
-- wrong on three counts, and none of them are about tidiness.
--
-- This table is PLATFORM-WIDE. Its terms are published into `stt_keywords` and `mt_glossary`
-- for every meeting in every workspace, and from there into prompts sent to external STT and
-- TTS providers. Seeding five individuals' names here would transmit personal data belonging
-- to one team into the request context of unrelated customers, permanently and by default.
--
-- It is also the wrong scope. Those names matter to exactly one workspace, and the workspace
-- glossary already exists for precisely that — MergeTerms even lets a workspace term override
-- a global one, so nothing is lost by putting them where they belong.
--
-- And it would degrade recognition for everybody else: the STT keyword budget is 24, so five
-- names nobody in that meeting will say would displace five terms they might, while biasing
-- the model toward phonemes that are not coming.
--
-- A team that wants its own names recognised should add them to its own workspace glossary.

BEGIN;

INSERT INTO transcript.global_glossary_terms
  (term, preferred_translation, source_language, target_language, business_domain,
   definition, priority, status)
VALUES
  -- ── This product ────────────────────────────────────────────────────────────
  ('WarpTalk',   'WarpTalk',   NULL, NULL, NULL, 'This product: an AI speech translation platform for real-time multilingual meetings.', 10, 'published'),
  ('WarpBot',    'WarpBot',    NULL, NULL, NULL, 'The in-meeting AI assistant in WarpTalk, addressed as @WarpBot.', 10, 'published'),
  ('SEP490',     'SEP490',     NULL, NULL, NULL, 'The capstone course code this project is delivered for.', 9, 'published'),
  ('FPT',        'FPT',        NULL, NULL, NULL, 'FPT University, where this capstone is presented.', 9, 'published'),

  -- ── Words the rehearsal actually mangled ────────────────────────────────────
  ('Codex',        'Codex',        NULL, NULL, NULL, 'An AI coding assistant. Transcribed as "cô đích" in a production rehearsal.', 10, 'published'),
  ('voice clone',  'voice clone',  NULL, NULL, NULL, 'Synthesising a speaker''s own voice for their translated audio. Transcribed as "Voiclon".', 10, 'published'),
  ('voice cloning','voice cloning',NULL, NULL, NULL, 'The act of producing a voice clone.', 9, 'published'),

  -- ── The stack, in roughly the order it comes up ─────────────────────────────
  ('LiveKit',     'LiveKit',     NULL, NULL, NULL, 'The WebRTC platform carrying WarpTalk audio and video.', 9, 'published'),
  ('SignalR',     'SignalR',     NULL, NULL, NULL, 'The realtime messaging library used for transcript and presence updates.', 8, 'published'),
  ('gRPC',        'gRPC',        NULL, NULL, NULL, 'The RPC framework used between WarpTalk backend services.', 8, 'published'),
  ('Redis',       'Redis',       NULL, NULL, NULL, 'The in-memory store carrying the AI pipeline streams.', 8, 'published'),
  ('PostgreSQL',  'PostgreSQL',  NULL, NULL, NULL, 'The relational database behind every WarpTalk service.', 8, 'published'),
  ('Postgres',    'Postgres',    NULL, NULL, NULL, 'Short form of PostgreSQL.', 7, 'published'),
  ('Docker',      'Docker',      NULL, NULL, NULL, 'The container runtime every WarpTalk service is deployed with.', 8, 'published'),
  ('Kubernetes',  'Kubernetes',  NULL, NULL, NULL, 'Container orchestration. Previously clipped to "Kuber" by a short VAD hangover.', 8, 'published'),
  ('Cartesia',    'Cartesia',    NULL, NULL, NULL, 'The text-to-speech provider producing WarpTalk''s dubbed audio.', 8, 'published'),
  ('Whisper',     'Whisper',     NULL, NULL, NULL, 'A speech recognition model family.', 7, 'published'),
  ('Next.js',     'Next.js',     NULL, NULL, NULL, 'The React framework the WarpTalk web client is built on.', 8, 'published'),
  ('TypeScript',  'TypeScript',  NULL, NULL, NULL, 'The language the WarpTalk web client is written in.', 7, 'published'),
  ('Tailwind',    'Tailwind',    NULL, NULL, NULL, 'The CSS framework used by the WarpTalk web client.', 6, 'published'),
  ('Grafana',     'Grafana',     NULL, NULL, NULL, 'The dashboard used to observe WarpTalk in production.', 6, 'published'),
  ('Prometheus',  'Prometheus',  NULL, NULL, NULL, 'The metrics system behind those dashboards.', 6, 'published'),
  ('Tailscale',   'Tailscale',   NULL, NULL, NULL, 'The private network production hosts are reached over.', 6, 'published'),
  ('Cloudflare',  'Cloudflare',  NULL, NULL, NULL, 'The provider fronting WarpTalk''s domains and object storage.', 6, 'published'),

  -- ── Vocabulary of the demo itself ───────────────────────────────────────────
  ('transcript',   'transcript',   NULL, NULL, NULL, 'The written record of what was said in a meeting.', 7, 'published'),
  ('workspace',    'workspace',    NULL, NULL, NULL, 'A WarpTalk tenant containing members, meetings and documents.', 7, 'published'),
  ('breakout room','breakout room',NULL, NULL, NULL, 'A sub-meeting split off from the main room.', 6, 'published'),
  ('glossary',     'glossary',     NULL, NULL, NULL, 'The term list biasing recognition and translation — this table.', 6, 'published')
ON CONFLICT DO NOTHING;

COMMIT;
