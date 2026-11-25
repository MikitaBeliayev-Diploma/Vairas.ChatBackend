-- PROPOSED overlay on generated schema.sql, empty isolated AI database only.
-- No owner-service SQL references. Not a populated upgrade migration.
-- Handler locks Conversation BEFORE issuing child UPDATE; all writers same order.
CREATE UNIQUE INDEX one_active_run ON assistant_run(school_id,conversation_id)
 WHERE status IN ('Queued','Running','AwaitingConfirmation');

CREATE FUNCTION ai_guard() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public, pg_temp AS $$
DECLARE c conversation; r assistant_run; i tool_invocation; msg chat_message;
BEGIN
 IF TG_OP = 'DELETE' THEN
   RAISE EXCEPTION 'AI append-only/lifecycle rows: controlled erasure procedure required';
 END IF;
 IF NOT isfinite(NEW.created_at) AND TG_TABLE_NAME <> 'action_confirmation' THEN
   RAISE EXCEPTION 'finite creation time required';
 END IF;
 IF TG_TABLE_NAME = 'ai_model_deployment' THEN
   IF TG_OP='UPDATE' AND (to_jsonb(NEW)-'enabled') IS DISTINCT FROM (to_jsonb(OLD)-'enabled') THEN
     RAISE EXCEPTION 'deployment version immutable; create new version';
   END IF;
   RETURN NEW;
 END IF;
 IF TG_TABLE_NAME = 'conversation' THEN
   IF TG_OP='INSERT' AND (NEW.revision<>1 OR NEW.status<>'Open') THEN RAISE EXCEPTION 'new conversation shape'; END IF;
   IF TG_OP='UPDATE' THEN
     IF (to_jsonb(NEW)-ARRAY['title','status','revision','archived_at','archive_request_id','archive_expected_revision']) IS DISTINCT FROM
        (to_jsonb(OLD)-ARRAY['title','status','revision','archived_at','archive_request_id','archive_expected_revision']) OR NEW.revision<>OLD.revision+1 THEN
       RAISE EXCEPTION 'conversation identity/revision immutable';
     END IF;
     IF OLD.status='Archived' OR NEW.status NOT IN ('Open','Archived') THEN RAISE EXCEPTION 'archive terminal'; END IF;
     IF NEW.status='Archived' AND EXISTS(SELECT 1 FROM assistant_run WHERE school_id=NEW.school_id AND conversation_id=NEW.id AND status IN ('Queued','Running','AwaitingConfirmation')) THEN
       RAISE EXCEPTION 'cancel active run before archive';
     END IF;
   END IF;
   RETURN NEW;
 END IF;
 SELECT * INTO c FROM conversation WHERE school_id=NEW.school_id AND id=NEW.conversation_id FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'conversation missing'; END IF;
 IF TG_TABLE_NAME='chat_message' THEN
   IF TG_OP='UPDATE' THEN RAISE EXCEPTION 'message append only'; END IF;
   IF c.status<>'Open' THEN RAISE EXCEPTION 'archived conversation'; END IF;
   IF NEW.sequence_no<>(SELECT COALESCE(MAX(sequence_no),0)+1 FROM chat_message WHERE school_id=NEW.school_id AND conversation_id=NEW.conversation_id) THEN
     RAISE EXCEPTION 'message sequence must be next';
   END IF;
   RETURN NEW;
 END IF;
 IF TG_TABLE_NAME='assistant_run' THEN
   IF TG_OP='INSERT' THEN
     IF c.status<>'Open' OR NEW.status<>'Queued' OR NEW.revision<>1 OR NEW.started_at IS NOT NULL THEN RAISE EXCEPTION 'new run shape'; END IF;
     PERFORM 1 FROM ai_model_deployment WHERE id=NEW.model_deployment_id AND enabled;
     IF NOT FOUND THEN RAISE EXCEPTION 'deployment disabled'; END IF;
   ELSE
     IF (to_jsonb(NEW)-ARRAY['status','revision','response_message_id','input_tokens','output_tokens','error_code','started_at','finished_at']) IS DISTINCT FROM
        (to_jsonb(OLD)-ARRAY['status','revision','response_message_id','input_tokens','output_tokens','error_code','started_at','finished_at']) OR NEW.revision<>OLD.revision+1 THEN RAISE EXCEPTION 'run identity/revision'; END IF;
     IF OLD.status IN ('Completed','Failed','Cancelled') THEN RAISE EXCEPTION 'run terminal'; END IF;
     IF NOT ((OLD.status='Queued' AND NEW.status IN ('Running','Failed','Cancelled')) OR
       (OLD.status='Running' AND NEW.status IN ('Running','AwaitingConfirmation','Completed','Failed','Cancelled')) OR
       (OLD.status='AwaitingConfirmation' AND NEW.status IN ('Running','Failed','Cancelled'))) THEN RAISE EXCEPTION 'run transition'; END IF;
     IF OLD.started_at IS NOT NULL AND NEW.started_at IS DISTINCT FROM OLD.started_at THEN RAISE EXCEPTION 'run start immutable'; END IF;
   END IF;
   SELECT * INTO msg FROM chat_message WHERE school_id=NEW.school_id AND conversation_id=NEW.conversation_id AND id=NEW.trigger_message_id;
   IF NOT FOUND OR msg.role<>'User' THEN RAISE EXCEPTION 'run trigger must be User'; END IF;
   IF NEW.status IN ('Running','AwaitingConfirmation','Completed') AND NEW.started_at IS NULL THEN RAISE EXCEPTION 'run start required'; END IF;
   IF NEW.status='Completed' THEN
     SELECT * INTO msg FROM chat_message WHERE school_id=NEW.school_id AND conversation_id=NEW.conversation_id AND id=NEW.response_message_id;
     IF NOT FOUND OR msg.role<>'Assistant' THEN RAISE EXCEPTION 'run response must be Assistant'; END IF;
     IF msg.sequence_no <= (SELECT sequence_no FROM chat_message WHERE school_id=NEW.school_id AND conversation_id=NEW.conversation_id AND id=NEW.trigger_message_id) THEN RAISE EXCEPTION 'response precedes trigger'; END IF;
     IF EXISTS(SELECT 1 FROM tool_invocation WHERE school_id=NEW.school_id AND conversation_id=NEW.conversation_id AND run_id=NEW.id AND status NOT IN ('Succeeded','Rejected','Failed','Expired','Cancelled') AND NOT (NEW.execution_profile='PreviewOnlyV1' AND effect='Write' AND status IN ('AwaitingConfirmation','Confirmed'))) THEN RAISE EXCEPTION 'unresolved tool prevents completion'; END IF;
   END IF;
   RETURN NEW;
 END IF;
 IF TG_TABLE_NAME='tool_invocation' THEN
   SELECT * INTO r FROM assistant_run WHERE school_id=NEW.school_id AND conversation_id=NEW.conversation_id AND id=NEW.run_id;
   IF NOT FOUND THEN RAISE EXCEPTION 'run missing'; END IF;
   IF TG_OP='INSERT' THEN
     IF c.status<>'Open' OR r.status<>'Running' OR NEW.revision<>1 OR NEW.started_at IS NOT NULL OR
       (NEW.effect='Read' AND NEW.status<>'Prepared') OR (NEW.effect='Write' AND NEW.status<>'AwaitingConfirmation') THEN RAISE EXCEPTION 'new invocation shape'; END IF;
     IF NEW.effect='Write' AND (EXISTS(SELECT 1 FROM tool_invocation WHERE school_id=NEW.school_id AND conversation_id=NEW.conversation_id AND run_id=NEW.run_id AND effect='Write') OR
       EXISTS(SELECT 1 FROM tool_invocation WHERE school_id=NEW.school_id AND conversation_id=NEW.conversation_id AND effect='Write' AND status IN ('Executing','Unknown'))) THEN RAISE EXCEPTION 'write admission blocked'; END IF;
   ELSE
     IF (to_jsonb(NEW)-ARRAY['status','revision','result_reference','result_summary','error_code','started_at','finished_at']) IS DISTINCT FROM
       (to_jsonb(OLD)-ARRAY['status','revision','result_reference','result_summary','error_code','started_at','finished_at']) OR NEW.revision<>OLD.revision+1 THEN RAISE EXCEPTION 'invocation payload/revision immutable'; END IF;
     IF NOT ((OLD.status='Prepared' AND NEW.status IN ('Executing','Rejected','Failed','Cancelled')) OR
       (OLD.status='AwaitingConfirmation' AND NEW.status IN ('Confirmed','Expired','Rejected','Cancelled')) OR
       (OLD.status='Confirmed' AND NEW.status IN ('Executing','Expired','Rejected','Cancelled')) OR
       (OLD.status='Executing' AND NEW.status IN ('Succeeded','Rejected','Failed','Unknown')) OR
       (OLD.status='Unknown' AND NEW.status IN ('Succeeded','Rejected','Failed'))) THEN RAISE EXCEPTION 'invocation transition'; END IF;
     IF OLD.started_at IS NOT NULL AND NEW.started_at IS DISTINCT FROM OLD.started_at THEN RAISE EXCEPTION 'invocation start immutable'; END IF;
     IF NEW.status='Executing' THEN
       IF NEW.effect='Write' AND r.execution_profile='PreviewOnlyV1' THEN RAISE EXCEPTION 'business dispatch disabled'; END IF;
       IF r.status<>'Running' OR c.status<>'Open' OR NEW.started_at IS NULL THEN RAISE EXCEPTION 'dispatch run/start'; END IF;
       IF NEW.effect='Write' AND (NEW.expires_at<=clock_timestamp() OR EXISTS(SELECT 1 FROM tool_invocation WHERE school_id=NEW.school_id AND conversation_id=NEW.conversation_id AND id<>NEW.id AND effect='Write' AND status IN ('Executing','Unknown'))) THEN RAISE EXCEPTION 'dispatch expired/blocked'; END IF;
     END IF;
     IF NEW.status='Expired' AND NEW.expires_at>clock_timestamp() THEN RAISE EXCEPTION 'not yet expired'; END IF;
     IF NEW.effect='Write' AND NEW.status IN ('Confirmed','Executing') THEN
       PERFORM 1 FROM action_confirmation WHERE school_id=NEW.school_id AND conversation_id=NEW.conversation_id AND invocation_id=NEW.id AND confirmed_digest=NEW.action_digest AND confirmed_by_user_id=c.owner_user_id AND confirmed_at<NEW.expires_at;
       IF NOT FOUND THEN RAISE EXCEPTION 'exact confirmation required'; END IF;
     END IF;
   END IF;
   RETURN NEW;
 END IF;
 RAISE EXCEPTION 'unknown guarded table';
END $$;

-- Confirmation table has no created_at; dedicated guard.
CREATE FUNCTION ai_confirm_guard() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public, pg_temp AS $$
DECLARE c conversation; i tool_invocation; r assistant_run;
BEGIN
 IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'confirmation immutable'; END IF;
 SELECT * INTO c FROM conversation WHERE school_id=NEW.school_id AND id=NEW.conversation_id FOR UPDATE;
 IF NOT FOUND OR c.status<>'Open' OR c.owner_user_id<>NEW.confirmed_by_user_id THEN RAISE EXCEPTION 'confirmation owner'; END IF;
 SELECT * INTO i FROM tool_invocation WHERE school_id=NEW.school_id AND conversation_id=NEW.conversation_id AND id=NEW.invocation_id FOR UPDATE;
 IF NOT FOUND OR i.effect<>'Write' OR i.status<>'AwaitingConfirmation' OR i.action_digest<>NEW.confirmed_digest OR i.expires_at<=clock_timestamp() THEN RAISE EXCEPTION 'confirmation basis/expiry'; END IF;
 SELECT * INTO r FROM assistant_run WHERE school_id=i.school_id AND conversation_id=i.conversation_id AND id=i.run_id;
 IF r.status NOT IN ('Running','AwaitingConfirmation') AND NOT (r.status='Completed' AND r.execution_profile='PreviewOnlyV1') THEN RAISE EXCEPTION 'confirmation run closed'; END IF;
 NEW.confirmed_at=clock_timestamp();
 RETURN NEW;
END $$;

CREATE FUNCTION ai_confirmation_final() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public, pg_temp AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM tool_invocation WHERE school_id=NEW.school_id AND conversation_id=NEW.conversation_id AND id=NEW.invocation_id AND status='AwaitingConfirmation') THEN
   RAISE EXCEPTION 'confirmation and status transition must commit together';
 END IF;
 RETURN NULL;
END $$;

CREATE TRIGGER ai_deployment_guard BEFORE INSERT OR UPDATE OR DELETE ON ai_model_deployment FOR EACH ROW EXECUTE FUNCTION ai_guard();
CREATE TRIGGER ai_conversation_guard BEFORE INSERT OR UPDATE OR DELETE ON conversation FOR EACH ROW EXECUTE FUNCTION ai_guard();
CREATE TRIGGER ai_message_guard BEFORE INSERT OR UPDATE OR DELETE ON chat_message FOR EACH ROW EXECUTE FUNCTION ai_guard();
CREATE TRIGGER ai_run_guard BEFORE INSERT OR UPDATE OR DELETE ON assistant_run FOR EACH ROW EXECUTE FUNCTION ai_guard();
CREATE TRIGGER ai_invocation_guard BEFORE INSERT OR UPDATE OR DELETE ON tool_invocation FOR EACH ROW EXECUTE FUNCTION ai_guard();
CREATE TRIGGER ai_confirmation_guard BEFORE INSERT OR UPDATE OR DELETE ON action_confirmation FOR EACH ROW EXECUTE FUNCTION ai_confirm_guard();
CREATE CONSTRAINT TRIGGER ai_confirmation_atomic AFTER INSERT ON action_confirmation DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ai_confirmation_final();

-- Grants are deployment-specific: app role must not own objects, disable triggers,
-- TRUNCATE, ALTER, run arbitrary SQL, or use the separate audited erasure role.
-- SQL cannot validate JWT/Active/School rights or prove remote receipt authenticity.
