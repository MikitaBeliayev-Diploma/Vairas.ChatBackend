-- Administrator-only, dedicated empty database, before --initialize.
-- Login roles and their passwords are provisioned externally.
ALTER SCHEMA public OWNER TO vairas_chat_migration;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE,CREATE ON SCHEMA public TO vairas_chat_migration;
