UPDATE assistant.plugins
SET
    label = 'Google Drive & Calendar',
    description = 'Work across Google Drive and Google Calendar.',
    tools_json = CASE
        WHEN EXISTS (
            SELECT 1
            FROM jsonb_array_elements(tools_json) AS tool
            WHERE tool->>'name' = 'google_drive_get_file'
        ) THEN tools_json
        ELSE tools_json || jsonb_build_array(
            jsonb_build_object(
                'name', 'google_drive_get_file',
                'pluginKey', 'google_workspace',
                'label', 'Read Google Drive file',
                'description', 'Read supported text content from a connected Google Drive file.',
                'effect', 'read',
                'requiredScopes', jsonb_build_array('https://www.googleapis.com/auth/drive.readonly'),
                'parameters', jsonb_build_object(
                    'type', 'object',
                    'properties', jsonb_build_object(
                        'fileId', jsonb_build_object('type', 'string')
                    ),
                    'required', jsonb_build_array('fileId')
                )
            )
        )
    END,
    updated_at = now()
WHERE plugin_key = 'google_workspace';
