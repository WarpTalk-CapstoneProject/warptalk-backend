ALTER TABLE translation_room.translation_room_artifacts
    ADD COLUMN IF NOT EXISTS provider_artifact_id varchar(255);

CREATE UNIQUE INDEX IF NOT EXISTS translation_room_artifacts_provider_artifact_id_key
    ON translation_room.translation_room_artifacts (provider_artifact_id)
    WHERE provider_artifact_id IS NOT NULL;
