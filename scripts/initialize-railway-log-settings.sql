-- Run once against the configuration database. Contains no credentials and never overwrites existing rows.
-- Then replace the masked token and settings through the deployment-administrator Database settings editor.
INSERT INTO app.configuration_settings(scope, key, value, is_secret, is_active, updated_at)
VALUES
('Api', 'RailwayLogs:Settings', '{"enabled":false,"projectId":"","environmentId":"","environment":"production","services":[]}', false, true, now()),
('Api', 'RailwayLogs:ProjectToken', '', true, false, now())
ON CONFLICT (scope, key) DO NOTHING;
