-- Explicit implementation delta; apply between frozen baseline and lifecycle guards.
ALTER TABLE conversation ADD COLUMN creation_request_id uuid NOT NULL;
ALTER TABLE conversation ADD COLUMN access_stamp varchar(64) NOT NULL CHECK(access_stamp ~ '^[0-9a-f]{64}$');
CREATE UNIQUE INDEX conversation_creation_key ON conversation(owner_user_id,creation_request_id);
ALTER TABLE assistant_run ADD COLUMN execution_profile varchar(32) NOT NULL CHECK(execution_profile='PreviewOnlyV1');
ALTER TABLE assistant_run ADD COLUMN deadline_at timestamptz NOT NULL CHECK(isfinite(deadline_at) AND deadline_at>created_at);
CREATE UNIQUE INDEX invocation_public_id ON tool_invocation(school_id,id);
CREATE UNIQUE INDEX one_active_preview ON tool_invocation(school_id,conversation_id) WHERE effect='Write' AND status IN ('AwaitingConfirmation','Confirmed');

CREATE FUNCTION chat_scope_school() RETURNS uuid LANGUAGE sql STABLE SET search_path=pg_catalog,public AS $$ SELECT nullif(current_setting('vairas.school_id',true),'')::uuid $$;
CREATE FUNCTION chat_scope_user() RETURNS uuid LANGUAGE sql STABLE SET search_path=pg_catalog,public AS $$ SELECT nullif(current_setting('vairas.user_id',true),'')::uuid $$;
ALTER TABLE conversation ENABLE ROW LEVEL SECURITY;
CREATE POLICY conversation_scope ON conversation USING(school_id=chat_scope_school() AND owner_user_id=chat_scope_user()) WITH CHECK(school_id=chat_scope_school() AND owner_user_id=chat_scope_user());
DO $$ DECLARE target text; BEGIN
 FOREACH target IN ARRAY ARRAY['chat_message','assistant_run','tool_invocation','action_confirmation'] LOOP
   EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY',target);
   EXECUTE format('CREATE POLICY private_scope ON %I USING(school_id=chat_scope_school() AND EXISTS(SELECT 1 FROM conversation c WHERE c.school_id=%I.school_id AND c.id=%I.conversation_id)) WITH CHECK(school_id=chat_scope_school() AND EXISTS(SELECT 1 FROM conversation c WHERE c.school_id=%I.school_id AND c.id=%I.conversation_id))',target,target,target,target,target);
 END LOOP;
END $$;

-- Narrow security-definer functions expose evidence only; never chat bodies/args.
CREATE FUNCTION chat_audit(request_school uuid,request_limit int)
RETURNS TABLE(operation_id uuid,tool text,outcome text,recorded_at timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT i.operation_id,i.tool_name::text,i.status::text,COALESCE(c.confirmed_at,i.created_at)
 FROM public.tool_invocation i LEFT JOIN public.action_confirmation c ON (c.school_id,c.conversation_id,c.invocation_id)=(i.school_id,i.conversation_id,i.id)
 WHERE i.school_id=request_school AND i.effect='Write'
 ORDER BY i.created_at DESC,i.id DESC LIMIT greatest(0,least(request_limit,100))
$$;
CREATE FUNCTION chat_confirmation(request_school uuid,request_conversation uuid,request_invocation uuid,request_identity uuid,request_operation uuid,request_digest text)
RETURNS TABLE(confirmed_at timestamptz,expires_at timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT a.confirmed_at,i.expires_at FROM public.action_confirmation a
 JOIN public.tool_invocation i ON (i.school_id,i.conversation_id,i.id)=(a.school_id,a.conversation_id,a.invocation_id)
 JOIN public.conversation c ON (c.school_id,c.id)=(i.school_id,i.conversation_id)
 WHERE (i.school_id,i.conversation_id,i.id)=(request_school,request_conversation,request_invocation)
 AND c.status='Open' AND i.status='Confirmed' AND i.expires_at>clock_timestamp()
 AND i.operation_id=request_operation AND i.action_digest=request_digest AND a.confirmed_digest=i.action_digest AND a.confirmed_by_user_id=c.owner_user_id
 AND i.preview->>'actorIdentityId'=request_identity::text
$$;
REVOKE ALL ON FUNCTION chat_audit(uuid,int),chat_confirmation(uuid,uuid,uuid,uuid,uuid,text) FROM PUBLIC;
