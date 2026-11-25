# Confirmed practical lesson actions

Через Chat можно создать практическое занятие и перенести существующее: **preview → точное согласие → Scheduling → owner receipt**. Оба действия сохраняют текущие права **Administrator**. Student self-booking, отмена занятия и перенос theory lessons через чат в этот профиль не входят.

## HTTP flow

Создать разговор с `POST /api/v1/chat/sessions`, `Idempotency-Key: <UUID>` и телом:

```json
{"schoolId":"<school UUID>","executionProfile":"ConfirmedLessonActionsV1"}
```

Модель использует прежние `preview_lesson_booking` и `preview_lesson_rescheduling`. Ответ preview содержит серверные `previewId`, `operationId`, `argumentsFingerprint`, срок и точные параметры. После явного действия пользователя отправить:

```http
POST /api/v1/chat/action-previews/{previewId}/executions?schoolId={schoolId}
Authorization: Bearer <current user token>
Origin: https://localhost:4200
Idempotency-Key: <operationId from preview>
Content-Type: application/json
```

```json
{"operationId":"<operationId from preview>","argumentsFingerprint":"<exact digest from preview>","approved":true}
```

Команда не принимает новые параметры занятия. Она использует сохранённый owner preview, текущую сессию, школу и пользователя. Перед отправкой повторяются admission и owner preview. Изменившаяся исходная версия, параметры или состояние ресурсов требуют нового preview и согласия.

Успешное создание возвращает `201`, перенос — `200`, со `status: Committed`, `lessonId`, `version` и `operationId`. `202` с `Executing`/`Unknown` означает, что результат ещё не установлен. `Rejected` возвращает сохранённый код owner rejection с HTTP 4xx. Ошибки admission/consent используют ProblemDetails. Повтор идентичного POST возвращает сохранённый результат; новый operationId для того же согласия запрещён.

`GET /api/v1/chat/action-previews/{previewId}/execution?schoolId={schoolId}` читает сохранённый статус под текущим private-chat admission. Обновление результата не создаёт нового сообщения модели. Перенос требует точных `lessonId`, `enrollmentId`, `expectedVersion`, UTC-интервала и причины из preview; Scheduling повторно проверяет revision и сохраняет исходные резервирования при конфликте.

Разговор без `executionProfile` получает `PreviewOnlyV1`. Его нельзя повысить до исполняемого профиля. Прежний POST `/confirmations` и запрос `/internal/v1/chat/confirmations/validate` сохраняют record-only поведение, включая `executionAllowed:false`.

## Ownership и токены

Chat сохраняет preview, согласие, намерение и статус. Scheduling владеет бизнес-правилами, занятием, версиями, reservation claims, историей, receipt и Outbox. Его бизнес-изменения и receipt фиксируются одной транзакцией.

В model catalogue остаются **шесть** read/preview tools. Для программного BFF-вызова доступны отдельные MCP-каталоги: один bound execution tool (`create_lesson_booking` или `reschedule_lesson_booking`) либо служебный `resolve_lesson_action`. Они не передаются модели. Контракты: [confirmed lesson tools](../../Vairas.McpServer/api/confirmed-lesson-tools.json).

Execution token exchange выполняется двумя переходами: `chat-backend` получает отдельный токен MCP, затем `mcp-server` — отдельный токен Scheduling. Profile `ConfirmedLessonActionsV1` связывает identity, session, user, school, conversation, preview, operation, action digest и digest точных owner arguments/source revisions. TTL не превышает 60 секунд и оставшийся срок исходного токена/сессии. ReadPreviewV1 не повышается до write scope. Gateway и Scheduling допускают только два маршрута `POST /api/v1/practical-lessons/confirmed-bookings` и `/confirmed-reschedulings` с точной привязкой тела и Idempotency-Key.

Scheduling отдельно вызывает workload-only `POST /internal/v1/chat/lesson-authorizations/validate`, scope `chat.lesson-actions.authorize`, caller `scheduling`. Этот контракт проверяет сохранённое намерение и точные параметры; legacy `/internal/v1/confirmations/validate` не используется для нового flow. Action digest preview и внутренний Scheduling idempotency fingerprint имеют разные назначения и не сравниваются между собой.

Vehicle предоставляет отдельный `/internal/v1/vehicles/{vehicleId}/scheduling-eligibility` snapshot и `/ownership?schoolId=...`; старый `/eligibility` сохраняет форму ответа. Snapshot содержит явный `GateSupported`. Vehicle gate передаёт schoolId при блокировке, чтении результата и release. При наличии будущих занятий деактивация отклоняется durable receipt Scheduling.

## Восстановление

Согласие и переход `Executing` сохраняются до сетевого вызова. Неопределённый результат после отправки остаётся `Unknown`, с `finished_at=NULL`. Завершение model run не завершает бизнес-действие. Архивация, отзыв сессии или потеря доступа не превращают возможный commit в ложную отмену.

Worker сканирует до 50 действий каждые 5 секунд: `Unknown` и `Executing` старше 30 секунд. Короткий lease `recovery_after` исключает постоянную обработку одной неудачной записи; следующая попытка доступна через 30 секунд. Узкая SQL-функция discovery возвращает только school/user/preview IDs; содержимое читается в соответствующем RLS scope. Пользовательские JWT не сохраняются.

Recovery credentials допускают только `chat-backend → MCP` (`mcp.lesson-actions.reconcile`) и `mcp-server → Scheduling` (`scheduling.lesson-actions.reconcile`). Scheduling повторно сверяет сохранённое намерение через Chat с `reconcile:true`; срок preview и текущий доступ не препятствуют установлению уже начатого результата.

`POST /internal/v1/lesson-actions/resolve` использует ту же native write lock, что и исполнение. Он возвращает существующий receipt либо сохраняет окончательный `Rejected/NOT_EXECUTED`, если receipt отсутствует. Запоздалая команда с этим operationId получает тот же rejection. Занятая блокировка или недоступный owner сохраняют неопределённость. Повторная отправка business command worker-ом запрещена. Новые write previews блокируются, пока в разговоре есть `Executing`/`Unknown`; read tools остаются доступны. Выдача результата пользователю повторно проверяет текущий доступ.

## Включение

Сначала применить additive EF migration `ConfirmedLessonActions` и актуальный [runtime-grants.sql](../database/runtime-grants.sql). Таблиц остаётся шесть. Миграция сохраняет существующим разговорам `PreviewOnlyV1`, добавляет неизменяемый conversation profile, operational recovery lease и узкие lifecycle guards.

Новые переключатели по умолчанию выключены:

| Сервис | Настройка |
|---|---|
| Chat | `Chat:ConfirmedLessonActionsEnabled=true`, существующий `Mcp:Enabled=true` |
| Authentication | `Authentication:Delegation:Enabled=true`, `Authentication:Delegation:ConfirmedLessonActionsEnabled=true`; `AllowTokenExchange=true` только для chat-backend и mcp-server |
| MCP | `Mcp:ConfirmedLessonActionsEnabled=true` |
| Scheduling | `Scheduling:ConfirmedLessonActionsEnabled=true`; HTTPS `Dependencies:VehicleBaseUrl` и `Dependencies:ChatBaseUrl` |
| Vehicle | рабочий `Scheduling:Enabled=true`, HTTPS `Dependencies:SchedulingBaseUrl=https://localhost:7300/` |

Дополнительные client scopes регистрируются в Authentication с отдельными password hashes; секреты задаются извне:

| Client | Дополнительные workload scopes |
|---|---|
| chat-backend | `mcp.lesson-actions.reconcile` |
| mcp-server | `scheduling.lesson-actions.reconcile` |
| scheduling | `vehicles.eligibility.read chat.lesson-actions.authorize` |
| vehicle-management | `scheduling.vehicle-gate` |

В локальных allowlists Chat разрешить caller `scheduling` со scope `chat.lesson-actions.authorize`; Scheduling — `mcp-server/scheduling.lesson-actions.reconcile` и `vehicle-management/scheduling.vehicle-gate`; Vehicle — `scheduling/vehicles.eligibility.read`. Существующие Auth/UM/School scopes сохраняются. В Scheduling workload `Dependencies:Scope` включить новый authorization scope; для Vehicle — актуальный `scheduling.vehicle-gate`. Recovery использует отдельные client_credentials запросы с одним scope.

Отключение execution switches блокирует новые команды, сохраняя recovery. `Chat:RecoveryWorkerEnabled=false` предназначен для управляемой остановки worker или изолированных тестов; по умолчанию worker включён. Модель по-прежнему выключена по умолчанию. Production deployment, реальные персональные данные и облачная модель этим этапом не подтверждаются.

## Проверка

```powershell
Set-Location D:\Projects\Vairas\Backend\Vairas.ChatBackend
./scripts/verify-confirmed-actions.ps1 -BuildImages
```

Скрипт сохраняет свежие TRX и [summary](../evidence/confirmed-lesson-actions/verification-summary.json), проверяет миграцию, OpenAPI, Gateway catalogue, parity общих contracts/policies, unit coverage и Docker. Сквозной тест использует реальные API Auth/User/School/Vehicle/Scheduling/Chat/MCP/Gateway и шесть PostgreSQL БД; модель и начальные legal rules — явные synthetic fixtures. Отдельный read-preview smoke сохраняет проверку Learning Tracking. PostgreSQL-тесты проверяют atomic dispatch, exact replay, устаревшие версии, потерю подтверждения commit и гонку recovery с запоздалой командой.
