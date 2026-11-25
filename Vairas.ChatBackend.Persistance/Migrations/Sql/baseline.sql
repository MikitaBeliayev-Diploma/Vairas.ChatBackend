-- Generated from the exact current DBML. PROPOSAL: empty dedicated database only.
CREATE TYPE "conversation_status" AS ENUM (
  'Open',
  'Archived'
);

CREATE TYPE "message_role" AS ENUM (
  'User',
  'Assistant'
);

CREATE TYPE "run_status" AS ENUM (
  'Queued',
  'Running',
  'AwaitingConfirmation',
  'Completed',
  'Failed',
  'Cancelled'
);

CREATE TYPE "tool_effect" AS ENUM (
  'Read',
  'Write'
);

CREATE TYPE "invocation_status" AS ENUM (
  'Prepared',
  'AwaitingConfirmation',
  'Confirmed',
  'Executing',
  'Succeeded',
  'Rejected',
  'Failed',
  'Unknown',
  'Expired',
  'Cancelled'
);

CREATE TABLE "ai_model_deployment" (
  "id" uuid PRIMARY KEY,
  "provider" varchar(80) NOT NULL,
  "model_name" varchar(160) NOT NULL,
  "model_version" varchar(100),
  "deployment_name" varchar(160) NOT NULL,
  "region" varchar(80) NOT NULL,
  "endpoint_config_key" varchar(200) NOT NULL,
  "credential_secret_ref" varchar(240) NOT NULL,
  "configuration_version" int NOT NULL,
  "enabled" boolean NOT NULL DEFAULT false,
  "created_at" timestamptz NOT NULL,
  CONSTRAINT "deployment_version_positive" CHECK (configuration_version > 0),
  CONSTRAINT "deployment_finite_time" CHECK (isfinite(created_at))
);

CREATE TABLE "conversation" (
  "school_id" uuid NOT NULL,
  "id" uuid NOT NULL,
  "owner_user_id" uuid NOT NULL,
  "title" varchar(200),
  "status" conversation_status NOT NULL,
  "revision" bigint NOT NULL DEFAULT 1,
  "created_at" timestamptz NOT NULL,
  "archived_at" timestamptz,
  CONSTRAINT "conversation_revision_positive" CHECK (revision > 0),
  CONSTRAINT "conversation_archive_shape" CHECK ((status = 'Archived') = (archived_at IS NOT NULL)),
  CONSTRAINT "conversation_time_order" CHECK (archived_at IS NULL OR archived_at >= created_at),
  CONSTRAINT "conversation_finite_time" CHECK (isfinite(created_at) AND (archived_at IS NULL OR isfinite(archived_at))),
  PRIMARY KEY ("school_id", "id")
);

CREATE TABLE "chat_message" (
  "school_id" uuid NOT NULL,
  "conversation_id" uuid NOT NULL,
  "id" uuid NOT NULL,
  "sequence_no" bigint NOT NULL,
  "role" message_role NOT NULL,
  "content" text NOT NULL,
  "client_request_id" uuid,
  "created_at" timestamptz NOT NULL,
  CONSTRAINT "message_sequence_positive" CHECK (sequence_no > 0),
  CONSTRAINT "message_request_role" CHECK ((role = 'User') = (client_request_id IS NOT NULL)),
  CONSTRAINT "message_finite_time" CHECK (isfinite(created_at)),
  PRIMARY KEY ("school_id", "conversation_id", "id")
);

CREATE TABLE "assistant_run" (
  "school_id" uuid NOT NULL,
  "conversation_id" uuid NOT NULL,
  "id" uuid NOT NULL,
  "trigger_message_id" uuid NOT NULL,
  "response_message_id" uuid,
  "attempt_no" int NOT NULL,
  "model_deployment_id" uuid NOT NULL,
  "prompt_version" varchar(100) NOT NULL,
  "status" run_status NOT NULL,
  "revision" bigint NOT NULL DEFAULT 1,
  "input_tokens" bigint,
  "output_tokens" bigint,
  "error_code" varchar(80),
  "created_at" timestamptz NOT NULL,
  "started_at" timestamptz,
  "finished_at" timestamptz,
  CONSTRAINT "run_counters_positive" CHECK (attempt_no > 0 AND revision > 0),
  CONSTRAINT "run_usage_nonnegative" CHECK ((input_tokens IS NULL OR input_tokens >= 0) AND (output_tokens IS NULL OR output_tokens >= 0)),
  CONSTRAINT "run_response_shape" CHECK ((status = 'Completed') = (response_message_id IS NOT NULL)),
  CONSTRAINT "run_finished_shape" CHECK ((status IN ('Completed','Failed','Cancelled')) = (finished_at IS NOT NULL)),
  CONSTRAINT "run_time_order" CHECK ((started_at IS NULL OR started_at >= created_at) AND (finished_at IS NULL OR finished_at >= COALESCE(started_at, created_at))),
  CONSTRAINT "run_finite_time" CHECK (isfinite(created_at) AND (started_at IS NULL OR isfinite(started_at)) AND (finished_at IS NULL OR isfinite(finished_at))),
  PRIMARY KEY ("school_id", "conversation_id", "id")
);

CREATE TABLE "tool_invocation" (
  "school_id" uuid NOT NULL,
  "conversation_id" uuid NOT NULL,
  "id" uuid NOT NULL,
  "run_id" uuid NOT NULL,
  "sequence_no" int NOT NULL,
  "tool_name" varchar(120) NOT NULL,
  "tool_contract_version" varchar(80) NOT NULL,
  "effect" tool_effect NOT NULL,
  "target_context" varchar(80) NOT NULL,
  "canonical_arguments" jsonb NOT NULL,
  "preview" jsonb,
  "expected_source_revisions" jsonb,
  "action_digest" char(64),
  "expires_at" timestamptz,
  "operation_id" uuid NOT NULL,
  "status" invocation_status NOT NULL,
  "revision" bigint NOT NULL DEFAULT 1,
  "result_reference" varchar(240),
  "result_summary" jsonb,
  "error_code" varchar(80),
  "created_at" timestamptz NOT NULL,
  "started_at" timestamptz,
  "finished_at" timestamptz,
  CONSTRAINT "invocation_counters_positive" CHECK (sequence_no > 0 AND revision > 0),
  CONSTRAINT "invocation_effect_shape" CHECK ((effect = 'Write' AND preview IS NOT NULL AND expected_source_revisions IS NOT NULL AND action_digest IS NOT NULL AND action_digest ~ '^[0-9a-f]{64}$' AND expires_at IS NOT NULL AND expires_at > created_at) OR (effect = 'Read' AND preview IS NULL AND action_digest IS NULL AND expires_at IS NULL)),
  CONSTRAINT "invocation_effect_status" CHECK ((effect = 'Read' AND status NOT IN ('AwaitingConfirmation','Confirmed','Expired')) OR (effect = 'Write' AND status <> 'Prepared')),
  CONSTRAINT "invocation_finished_shape" CHECK ((status IN ('Succeeded','Rejected','Failed','Expired','Cancelled')) = (finished_at IS NOT NULL)),
  CONSTRAINT "invocation_time_order" CHECK ((started_at IS NULL OR started_at >= created_at) AND (finished_at IS NULL OR finished_at >= COALESCE(started_at,created_at))),
  CONSTRAINT "invocation_finite_time" CHECK (isfinite(created_at) AND (started_at IS NULL OR isfinite(started_at)) AND (finished_at IS NULL OR isfinite(finished_at)) AND (expires_at IS NULL OR isfinite(expires_at))),
  PRIMARY KEY ("school_id", "conversation_id", "id")
);

CREATE TABLE "action_confirmation" (
  "school_id" uuid NOT NULL,
  "conversation_id" uuid NOT NULL,
  "invocation_id" uuid NOT NULL,
  "confirmed_by_user_id" uuid NOT NULL,
  "confirmed_digest" char(64) NOT NULL,
  "confirmed_at" timestamptz NOT NULL,
  CONSTRAINT "confirmation_digest_shape" CHECK (confirmed_digest ~ '^[0-9a-f]{64}$'),
  CONSTRAINT "confirmation_finite_time" CHECK (isfinite(confirmed_at)),
  PRIMARY KEY ("school_id", "conversation_id", "invocation_id")
);

CREATE INDEX ON "conversation" ("school_id", "owner_user_id", "created_at");

CREATE UNIQUE INDEX ON "chat_message" ("school_id", "conversation_id", "sequence_no");

CREATE UNIQUE INDEX ON "chat_message" ("school_id", "conversation_id", "client_request_id");

CREATE UNIQUE INDEX ON "assistant_run" ("school_id", "conversation_id", "trigger_message_id", "attempt_no");

CREATE UNIQUE INDEX ON "assistant_run" ("school_id", "conversation_id", "response_message_id");

CREATE UNIQUE INDEX ON "tool_invocation" ("school_id", "conversation_id", "run_id", "sequence_no");

CREATE UNIQUE INDEX ON "tool_invocation" ("school_id", "operation_id");

CREATE INDEX ON "tool_invocation" ("status", "created_at");

COMMENT ON TABLE "ai_model_deployment" IS 'Global immutable configuration version, except enabled; no weights or API keys.';

COMMENT ON COLUMN "ai_model_deployment"."model_version" IS 'Null if provider does not expose fixed version; never invent one.';

COMMENT ON COLUMN "ai_model_deployment"."endpoint_config_key" IS 'Allowlisted server configuration, never a model-selected URL.';

COMMENT ON COLUMN "ai_model_deployment"."credential_secret_ref" IS 'Secret-manager reference only.';

COMMENT ON COLUMN "conversation"."school_id" IS 'External School Management ID; immutable.';

COMMENT ON COLUMN "conversation"."owner_user_id" IS 'External authenticated User ID; immutable.';

COMMENT ON TABLE "chat_message" IS 'Append-only owned child. PostgreSQL default unique permits multiple null client_request_id values.';

COMMENT ON COLUMN "chat_message"."content" IS 'Final protected display text; no hidden reasoning.';

COMMENT ON COLUMN "chat_message"."client_request_id" IS 'Required for User, null for Assistant; message API deduplication.';

COMMENT ON TABLE "assistant_run" IS 'Add partial unique active-run index; see context. Trigger message must have User role.';

COMMENT ON COLUMN "assistant_run"."response_message_id" IS 'Set atomically on completion; same-conversation Assistant message.';

COMMENT ON COLUMN "assistant_run"."input_tokens" IS 'Nonnegative if reported; null means unknown.';

COMMENT ON COLUMN "tool_invocation"."canonical_arguments" IS 'Immutable validated payload; protect sensitive fields.';

COMMENT ON COLUMN "tool_invocation"."preview" IS 'Exact displayed owner preview; required on Write.';

COMMENT ON COLUMN "tool_invocation"."expected_source_revisions" IS 'Owner source basis according to tool contract.';

COMMENT ON COLUMN "tool_invocation"."action_digest" IS 'SHA-256 canonical consent envelope; required on Write.';

COMMENT ON COLUMN "tool_invocation"."expires_at" IS 'Required on Write; first-dispatch deadline.';

COMMENT ON COLUMN "tool_invocation"."operation_id" IS 'Stable owner correlation/recovery key; unchanged on timeout.';

COMMENT ON COLUMN "tool_invocation"."result_reference" IS 'Owner receipt/result reference, not public capability URL.';

COMMENT ON COLUMN "tool_invocation"."result_summary" IS 'Minimal protected result.';

COMMENT ON TABLE "action_confirmation" IS 'Zero-or-one immutable owned child. Check same actor/digest and unexpired Write. Never model-generated consent.';

COMMENT ON COLUMN "action_confirmation"."confirmed_by_user_id" IS 'Authenticated conversation owner; checked by handler.';

ALTER TABLE "chat_message" ADD FOREIGN KEY ("school_id", "conversation_id") REFERENCES "conversation" ("school_id", "id") DEFERRABLE INITIALLY IMMEDIATE;

ALTER TABLE "assistant_run" ADD FOREIGN KEY ("school_id", "conversation_id") REFERENCES "conversation" ("school_id", "id") DEFERRABLE INITIALLY IMMEDIATE;

ALTER TABLE "assistant_run" ADD FOREIGN KEY ("school_id", "conversation_id", "trigger_message_id") REFERENCES "chat_message" ("school_id", "conversation_id", "id") DEFERRABLE INITIALLY IMMEDIATE;

ALTER TABLE "assistant_run" ADD FOREIGN KEY ("school_id", "conversation_id", "response_message_id") REFERENCES "chat_message" ("school_id", "conversation_id", "id") DEFERRABLE INITIALLY IMMEDIATE;

ALTER TABLE "assistant_run" ADD FOREIGN KEY ("model_deployment_id") REFERENCES "ai_model_deployment" ("id") DEFERRABLE INITIALLY IMMEDIATE;

ALTER TABLE "tool_invocation" ADD FOREIGN KEY ("school_id", "conversation_id", "run_id") REFERENCES "assistant_run" ("school_id", "conversation_id", "id") DEFERRABLE INITIALLY IMMEDIATE;

ALTER TABLE "action_confirmation" ADD FOREIGN KEY ("school_id", "conversation_id", "invocation_id") REFERENCES "tool_invocation" ("school_id", "conversation_id", "id") DEFERRABLE INITIALLY IMMEDIATE;

