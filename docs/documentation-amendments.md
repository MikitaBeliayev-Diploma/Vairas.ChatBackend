# Staged documentation amendments — 2026-11-24

Additive implementation extension: [Confirmed lesson creation and rescheduling](confirmed-lesson-actions.md). Existing PreviewOnlyV1 and record-only confirmation contracts are preserved. Central Documentation files have not been changed; these implementation deltas are staged here for adoption.


Это implementation note внутри Backend. `D:\Projects\Vairas\Documentation` и diploma requirements не перезаписывались. Источники: Communication/reference/chat.md, Communication/Api/chat.openapi.yaml, Communication/mcp/tools.json, AI/MCP architecture и frozen diploma AI Assistance schema. Central export ещё описывает часть будущего MCP write flow; его нельзя считать точным описанием этого профиля.

| Target | Требуемая редакция при следующем обновлении центральной документации |
|---|---|
| `Communication/Api/chat.openapi.yaml` | Перенести фактический [Chat export](../api/chat.openapi.yaml), десять public operations и отдельный internal recorded-intent query |
| `Communication/reference/chat.md` | Указать synchronous JSON, exact Origin/consent, private history, access epochs, terminal archive, cursor с beforeId |
| `Communication/mcp/tools.json` | Сохранить full target scope; отдельно отметить реализованные шесть BFF tools без MCP transport и business dispatch. Availability schema дополнить обязательными actual owner branch/teacher/category/duration fields; не объявлять старый tool contract совместимым автоматически |
| `Communication/Api/scheduling.openapi.yaml` | Перенести актуальный sibling export с двумя read-only owner preview operations |
| `Communication/Api/school-management.openapi.yaml` | Добавить internal assistant access projection со scope `school.assistant.read`, без gateway публикации |
| Gateway / architecture coverage | Девять owner services, 154 explicit public/metadata operations, Chat port 7800, public audience vairas-chat |
| Database / AI Assistance | Шесть baseline tables плюс operational columns и явно названный PreviewOnlyV1 guard delta; approval после Completed run допустим, execution write запрещён |

Legacy profile confirmation validation (`validateExactProfileConfirmation`, requestFingerprint/commandName) и фактический recorded-preview query — разные контракты. Профиль не реализует CreateUser/approved booking/approved rescheduling. Отдельный MCP server, безопасное user delegation, owner dispatch/idempotency recovery и end-to-end live providers остаются будущей работой; D-042/D-044 статусы принятия других контекстов сохраняются.

## Requirements mapping

Требования `D:\Diplom\docs\course-diploma-projects-docs-main\04-technical-requirements\04c-backend\backend.md` сопоставлены с implementation:

| Требование | Свидетельство |
|---|---|
| Modern framework, layered architecture, ORM | ASP.NET Core 10, Domain/Business/Persistance/HTTP, EF Core/Npgsql |
| State database | PostgreSQL migrations, six tables, native guards/RLS; [PG tests](../Vairas.ChatBackend.Tests/PostgresTests.cs) |
| API / centralized errors / structured logs | OpenAPI + ProblemDetails Boundary + JSON console/OTel |
| Unit coverage ≥70% business logic | Полные Domain и Business assemblies, unit-only collector, независимый floor в [verify.ps1](../scripts/verify.ps1) |
| Security and authorization | RS256/session/profile/School checks, owner authorization, exact confirmation, restricted runtime |
| Production deployment | Docker build и локальный startup проверяются отдельно; production deployment этим increment не выполнен |

BA US-018/019/020 поддерживаются здесь частично: chat interaction, current context, advisory previews и recorded human intent. Готовность всего AI/MCP business workflow не заявляется.

??????????? ??????????: ?????????? MCP transport ? read/preview delegation ??????????? ? sibling Vairas.McpServer; ???????? ? ??????? ?????????????? ? [MCP README](../../Vairas.McpServer/README.md). Write delegation/dispatch ? ??????? Foundry agent ???????? ??????? ??????.
