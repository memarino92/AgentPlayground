BEGIN;
-- Required tables fail closed if an unexpected/old schema is supplied.
TRUNCATE app.configuration_settings;
TRUNCATE agent_memory.mobile_device_tokens, agent_memory.agent_approvals;
DO $reset$
BEGIN
    IF to_regclass('app.data_protection_keys') IS NOT NULL THEN
        TRUNCATE app.data_protection_keys;
    END IF;
END
$reset$;
DROP SCHEMA IF EXISTS transport CASCADE;
INSERT INTO app.configuration_settings (scope,key,value,is_secret,is_active,updated_at) VALUES
    ('Api','PushNotifications:Enabled','false',false,true,now()),
    ('Api','Tavily:EnableWebSearch','false',false,true,now());
INSERT INTO agent_memory.tool_role_permissions (role_name,tool_key,is_enabled,updated_at,updated_by)
SELECT role_name, tool_key, false, now(), 'local-snapshot-reset'
FROM (VALUES ('Owner'),('Coach')) AS roles(role_name)
CROSS JOIN (VALUES ('Local:publish_mobile_notification'),('Local:sync_work_journal'),('Local:schedule_notification'),('Local:schedule_agent_task')) AS tools(tool_key)
ON CONFLICT (role_name,tool_key) DO UPDATE SET is_enabled=false, updated_at=now(), updated_by='local-snapshot-reset';
COMMIT;
