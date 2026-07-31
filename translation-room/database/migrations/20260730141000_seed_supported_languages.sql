INSERT INTO translation_room.supported_languages (
    code,
    name,
    native_name,
    is_active
)
VALUES
    ('vi-VN', 'Vietnamese', 'Tiếng Việt', TRUE),
    ('en-US', 'English', 'English', TRUE),
    ('ja-JP', 'Japanese', '日本語', TRUE),
    ('ko-KR', 'Korean', '한국어', TRUE),
    ('zh-CN', 'Chinese', '中文', TRUE),
    ('fr-FR', 'French', 'Français', TRUE),
    ('es-ES', 'Spanish', 'Español', TRUE)
ON CONFLICT (code) DO UPDATE
SET
    name = EXCLUDED.name,
    native_name = EXCLUDED.native_name,
    is_active = EXCLUDED.is_active;
