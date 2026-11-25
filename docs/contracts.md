# PreviewOnlyV1 contracts

Additive implementation extension: [Confirmed lesson creation and rescheduling](confirmed-lesson-actions.md). Existing PreviewOnlyV1 and record-only confirmation contracts are preserved. Central Documentation files have not been changed; these implementation deltas are staged here for adoption.


Authoritative export: [OpenAPI](../api/chat.openapi.yaml). Все public вызовы требуют RS256 user JWT с `sub`, `sid`, issuer Authentication и audience `vairas-chat`. `schoolId` обязателен; userId/identityId не принимаются из browser body или actor headers. Оригинальный bearer эфемерно передаётся owner API через Gateway, workload token используется только для Auth session validation и School assistant access.

Каждая операция повторно проверяет Auth session, активный User Management profile и текущее School membership/permission projection. История видна только владельцу. School access stamp привязывает разговор к конкретному набору memberships/grants; изменение закрывает старую сессию. Новое членство не открывает её повторно. Administrator audit содержит только operation ID/tool/outcome/time и не даёт права читать сообщения, arguments или preview другого пользователя.

| Operation | Контракт |
|---|---|
| `POST /api/v1/chat/sessions` | `{schoolId}`, UUID `Idempotency-Key`, 201 с Location |
| `GET /api/v1/chat/sessions` | `schoolId`, `limit` 1..100; cursor — **оба** `before` и `beforeId` из последней строки предыдущей страницы; order createdAt/id descending |
| `GET /api/v1/chat/sessions/{id}` | Приватная session metadata |
| `GET /api/v1/chat/sessions/{id}/messages` | `after` sequence number, `limit` 1..100; ascending sequence |
| `POST /api/v1/chat/sessions/{id}/messages` | `{text}`, UUID Idempotency-Key; синхронный JSON `{messageId,text,previewId,runId}` |
| `GET /api/v1/chat/sessions/{id}/runs/{runId}` | Durable outcome / error code без reasoning |
| `POST /api/v1/chat/sessions/{id}/archive` | `{expectedRevision}`, UUID Idempotency-Key; terminal archive, exact replay |
| `GET /api/v1/chat/action-previews/{id}` | Server envelope/digest/operationId/expiry для review |
| `POST /api/v1/chat/action-previews/{id}/confirmations` | `{operationId,argumentsFingerprint,approved:true}`; Idempotency-Key равен operationId; точный доверенный Origin |
| `GET /api/v1/schools/{schoolId}/assistant-audit` | Свежий School administrator доступ, `limit` 1..100 |

Все URL кроме create требуют `schoolId` query, кроме audit с schoolId в path. UUID keys непустые. Changed intent с прежним key → 409. Failed/interrupted message не перезапускается по тому же key; клиент читает run/history и явно отправляет новое намерение. Concurrent run в одном разговоре → 409. Client cancellation, timeout и late model answer не завершают прерванный run задним числом.

Пределы: HTTP body 64 KiB, user text 16 KiB, assistant text/tool result 32 KiB, owner response 512 KiB, четыре tool calls и один preview на run, model call 20 секунд, run 60 секунд, preview 300 секунд. Gateway message deadline 75 секунд. Server не повторяет model calls, business commands или owner writes. Rate limit сообщений 20/min/user, queue отсутствует; Gateway применяет свои лимиты независимо. Counters локальны для процесса.

## Tools и owners

| Tool | Owner / поведение |
|---|---|
| `get_my_enrollments` | School public self enrollments, фильтр текущей школы, максимум 50 |
| `get_my_lessons` | Scheduling self lessons, school/date scope, максимум 50 |
| `get_student_training_progress` | Learning Tracking progress с owner authorization |
| `find_available_lessons` | Scheduling availability; branch/teacher/category/date window/duration обязательны |
| `preview_lesson_booking` | Новый Scheduling `POST /api/v1/practical-lessons/booking-previews` |
| `preview_lesson_rescheduling` | Новый Scheduling `POST /api/v1/practical-lessons/{lessonId}/rescheduling-previews` |

Scheduling preview использует текущие правила, authorization, resource revisions и native reservation reads; не вызывает mutating Evaluate и не создаёт lesson/reservation/operation/event. Booking/rescheduling permissions остаются owner permissions (в текущем Scheduling — Administrator). Preview ничего не резервирует. Изменение времени/ресурса/правил/занятости требует нового preview. Версия owner response `scheduling-preview/1`, стабильный semantic basis без короткоживущего `validUntil`.

## Exact consent

Сервер выделяет invocation/operation IDs **до** digest. Envelope `vairas-chat-preview-consent/1` содержит school/conversation/user/identity/tool IDs, tool и owner preview versions, нормализованные arguments, source revisions, display, expiry и operationId. SHA-256 вычисляется по canonical JSON: sorted object keys, точное UTF-8 escaping, integer-only RFC 8785 subset. Floating point и числа вне безопасного integer range отвергаются; UTC timestamps — строки. Это не обещание поддержки произвольного RFC 8785 input.

Human approval поступает только с отдельного HTTP endpoint с доверенным Origin; такого tool у модели нет. Перед первым сохранением сервер повторяет owner preview и сравнивает arguments/revisions/display. Новое намерение отменяет прежний активный preview. PostgreSQL guard сохраняет точное server confirmation time и запрещает UPDATE/DELETE consent. Повтор возвращает исходный outcome/time. После expiry/archive/supersession metadata факта подтверждения не превращается в право исполнения.

`POST /internal/v1/chat/confirmations/validate` требует workload audience `vairas-chat-internal`, scope `confirmations.validate` и local allowlist. Request связывает `schoolId,sessionId,previewId,identityId,operationId,argumentsFingerprint`. Response: `{confirmed,executionAllowed:false,confirmedAt,expiresAt}`. Это read-only проверка действующей записи, **не authorization для business command**. Legacy `/internal/v1/confirmations/validate` не реализован и не является alias. Existing UM/MCP write callbacks не следует направлять на новый endpoint.

## Хранение и privacy

Шесть исходных таблиц: `ai_model_deployment`, `conversation`, `chat_message`, `assistant_run`, `tool_invocation`, `action_confirmation`. Добавлены creation/archive keys, access stamp, bounded deadline и execution profile; operational seventh table не создавалась. RLS использует transaction-local school/user settings. Advisory lock сериализует короткие транзакции одного пользователя в школе; network/model calls выполняются вне транзакций. Partial index ограничивает один активный preview. Guard прямо запрещает Write → Executing в `PreviewOnlyV1`.

Сообщения пользователя и финальный ответ сохраняются; tool arguments и immutable preview хранятся для consent. Raw tool facts, prompts, hidden reasoning, bearer, cookies и credential values не записываются в логи или result summary. Model deployment содержит references, а не secret values. Structured logs показывают endpoint/status/duration/correlation; OTel не подключает GenAI payload capture. Retention/purge и шифрование/backup/доступ к БД должны быть определены перед реальными данными; runtime не имеет общего DELETE API.
