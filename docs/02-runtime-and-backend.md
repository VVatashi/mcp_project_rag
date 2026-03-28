# Runtime And Backend

## Точка входа

Главная точка входа — [src/index.ts](../src/index.ts).

При старте процесс делает следующее:

1. Загружает конфигурацию через `loadConfig`.
2. Создаёт сервисы нижнего уровня.
3. Собирает `deps` для MCP и browser API.
4. В зависимости от `transport` запускает:
   `StdioServerTransport` или HTTP server.

## Конфигурация

Конфигурация описана в [src/config.ts](../src/config.ts).

Текущие свойства `AppConfig`:

- `transport`
- `httpHost`
- `httpPort`
- `httpPath`
- `dataDir`
- `registryFile`
- `indexStateDir`
- `modelCacheDir`
- `chromaUrl`
- `embeddingModel`
- `rerankModel`
- `maxContextChars`

### Как работает загрузка конфига

1. Читается `.env`, если файл существует.
2. Значения из `process.env` имеют приоритет.
3. Аргумент `--transport=` может переопределить тип транспорта.
4. `dataDir`, `indexStateDir` и `modelCacheDir` создаются заранее.

### Почему это решение уместно

- Поддерживает локальный запуск без дополнительного config service.
- Не требует отдельного bootstrap для Docker и local dev.
- Удобно для CLI и ручного старта в PowerShell.

## Композиция зависимостей

Текущий dependency graph в рантайме:

- `ProjectRegistry`
- `TransformersEmbeddingFunction`
- `ChromaProjectStore`
- `ProjectIndexStateCache`
- `ProjectOperationLock`
- `NeuralReranker`
- `ContextBuilderService`
- `IndexingService`

Эти зависимости затем передаются как `ServerDeps` в MCP server и HTTP server.

## MCP transport

MCP tools регистрируются в [src/mcp/create-server.ts](../src/mcp/create-server.ts).

Поддерживаемые tools:

- `projects.list`
- `projects.register`
- `projects.unregister`
- `index.run`
- `search.query`
- `context.build`

### Решение по MCP контракту

Каждый tool:

- имеет `inputSchema`;
- имеет `outputSchema`;
- возвращает и `content`, и `structuredContent`.

Это полезно сразу в двух плоскостях:

- агент может читать человекочитаемый текст;
- код и интеграции могут использовать структурированный ответ.

## HTTP transport

HTTP orchestration находится в [src/http/start-http-server.ts](../src/http/start-http-server.ts).

Файл теперь отвечает только за:

- создание `node:http` сервера;
- high-level routing между health, static UI, browser API и MCP endpoint;
- управление MCP sessions через `mcp-session-id`;
- обработку ошибок верхнего уровня.

### Вспомогательные HTTP модули

- [src/http/browser-api.ts](../src/http/browser-api.ts) — JSON API для Web UI.
- [src/http/browser-api-schemas.ts](../src/http/browser-api-schemas.ts) — schema layer и валидация browser API payloads через `zod`.
- [src/http/http-utils.ts](../src/http/http-utils.ts) — чтение JSON и отправка JSON ответов.
- [src/http/http-error.ts](../src/http/http-error.ts) — контролируемые HTTP ошибки.
- [src/http/static-files.ts](../src/http/static-files.ts) — раздача `public/*` и защита от path traversal.

### Почему transport разделён именно так

- MCP transport и browser API имеют разные требования к UX и формату.
- Разделение позволяет не смешивать JSON-RPC lifecycle и обычные CRUD запросы.
- Вынесение browser API в отдельный модуль сделало HTTP слой проще сопровождать.

## Browser API

Текущие маршруты для Web UI:

- `GET /api/projects`
- `POST /api/projects`
- `GET /api/projects/{projectId}`
- `PUT /api/projects/{projectId}`
- `DELETE /api/projects/{projectId}`
- `POST /api/projects/{projectId}/reindex`
- `POST /api/projects/{projectId}/search`
- `POST /api/projects/{projectId}/context`

### Логика маршрутов

Browser API не хранит отдельного состояния. Каждый маршрут напрямую использует сервисы из `ServerDeps`.

Примеры:

- create/update работают через `ProjectRegistry.register` и `ProjectRegistry.update`;
- delete использует `ChromaProjectStore.deleteProject`, `ProjectIndexStateCache.delete` и `ProjectRegistry.unregister`;
- reindex вызывает `IndexingService.run`;
- search/context используют `ContextBuilderService`.

Валидация payloads больше не размазана по route handlers вручную. Она вынесена в schema layer на базе `zod`, а ошибки схемы превращаются в единообразные `400` ответы.

Все mutating операции (`register`, `update`, `delete`, `reindex`) проходят через `ProjectOperationLock`. Если для того же `projectId` уже идёт другая мутация, сервер возвращает конфликт вместо конкурентного выполнения.

## Ошибки и устойчивость

Текущие решения:

- битый JSON возвращает `400`, а не общий `500`;
- path traversal через `/ui/*` блокируется;
- `projects.json` и `index-state/*.json` записываются через atomic write;
- повреждённый `index-state` автоматически убирается в `.corrupt-*`, после чего проект может быть переиндексирован заново;
- неожиданные ошибки логируются и конвертируются в generic `500`.

Это не полноценная security model, но это уже хороший базовый уровень для локального/внутреннего сценария.
