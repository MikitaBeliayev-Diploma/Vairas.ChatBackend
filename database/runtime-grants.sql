-- Provision these login roles externally; never embed credentials here.
REVOKE ALL ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC;
DO $$ BEGIN EXECUTE format('REVOKE CREATE,TEMPORARY ON DATABASE %I FROM PUBLIC',current_database()); END $$;
GRANT USAGE ON SCHEMA public TO vairas_chat_runtime,vairas_chat_operator;
GRANT SELECT ON conversation,chat_message,assistant_run,tool_invocation,action_confirmation,ai_model_deployment TO vairas_chat_runtime;
GRANT INSERT ON conversation,chat_message,assistant_run,tool_invocation,action_confirmation TO vairas_chat_runtime;
GRANT UPDATE(title,status,revision,archived_at,archive_request_id,archive_expected_revision) ON conversation TO vairas_chat_runtime;
GRANT UPDATE(status,revision,response_message_id,input_tokens,output_tokens,error_code,started_at,finished_at) ON assistant_run TO vairas_chat_runtime;
GRANT EXECUTE ON FUNCTION chat_pending_executions(timestamptz,int) TO vairas_chat_runtime;
GRANT UPDATE(recovery_after,status,revision,result_reference,result_summary,error_code,started_at,finished_at) ON tool_invocation TO vairas_chat_runtime;
GRANT EXECUTE ON FUNCTION chat_scope_school(),chat_scope_user(),ai_guard(),ai_confirm_guard(),ai_confirmation_final() TO vairas_chat_runtime;
GRANT EXECUTE ON FUNCTION chat_audit(uuid,int),chat_confirmation(uuid,uuid,uuid,uuid,uuid,text) TO vairas_chat_runtime;
GRANT SELECT ON ai_model_deployment TO vairas_chat_operator;
GRANT UPDATE(enabled) ON ai_model_deployment TO vairas_chat_operator;
-- No app DELETE/TRUNCATE/DDL/deployment writes and no private-chat reads for operator.
