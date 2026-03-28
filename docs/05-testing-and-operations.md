# Testing And Operations

## Набор тестов

Проект использует `vitest`. Основные области покрытия:

- конфигурация;
- registry;
- file discovery;
- chunking;
- rerank;
- context builder;
- indexer;
- HTTP feature flow.

Тесты лежат в каталоге [tests](../tests/).

## Текущий smoke status

На текущем этапе проект уже был прогнан живым smoke-сценарием поверх реального HTTP runtime:

- загрузка `GET /ui` в браузере;
- создание проекта через Web UI;
- повторные `POST /api/projects/{projectId}/reindex` с проверкой перехода `full -> incremental`;
- изменение файла на диске и повторная инкрементальная реиндексация;
- `initialize` и `tools/call` через MCP HTTP transport.

После P0-усилений дополнительно подтверждён сценарий:

- `GET /ui`;
- `POST /api/projects`;
- `POST /api/projects/{projectId}/reindex` с переходом `full -> incremental`;
- `tools/call search.query` поверх MCP HTTP transport на том же smoke-проекте.

Теперь в репозитории также есть автоматический browser-level smoke:

- `npm run test:e2e`;
- реальный UI поднимается через `startHttpServer` с mock-deps;
- Playwright проходит flow `create -> update -> reindex -> search -> delete`.

## Что проверяют ключевые тесты

### `http-feature.test.ts`

Это основной integration-like smoke test для HTTP режима.

Он проверяет:

- `GET /health`
- `GET /ui`
- browser API для projects
- create/update/reindex/search/context/delete flow
- расширенные stats индексации в live HTTP flow
- diagnostics для `inspect` / `reindex` ошибок и timestamp их последнего наблюдения
- invalid JSON handling
- schema validation errors для browser API payloads
- traversal protection для `/ui/*`
- MCP initialize/tools/list/tools/call поверх HTTP transport

### `indexer.test.ts`

Проверяет orchestration индексации:

- проект берётся из registry;
- discovery вызывается с правильным проектом;
- chunker вызывается по файлам;
- store получает итоговые чанки;
- registry получает `updateIndexStats`.
- full rebuild сохраняет manifest (`chunkIds/chunkHashes`) в state cache.
- persisted state теперь также хранит `contentHash`, чтобы переиндексация не зависела только от `mtime/size`.
- incremental path удаляет старые чанки по persisted `chunkIds`, не читая всю collection.
- incremental path перечитывает файлы, чтобы пересчитать `contentHash` и не пропустить stale-index сценарии.
- старый state без manifest приводит к безопасному fallback в `full`.

### `project-registry.test.ts`

Проверяет:

- создание проекта;
- обновление проекта;
- сохранение `createdAt`;
- отсутствие дублирования default excludes при update;
- обновление index stats;
- удаление проекта.

### `project-index-state-cache.test.ts`

Проверяет recovery-сценарий:

- повреждённый `index-state` не роняет runtime;
- файл переносится в `.corrupt-*`;
- сервис возвращает `undefined`, чтобы следующий `reindex` мог восстановить состояние.

### `project-operation-lock.test.ts`

Проверяет защиту от гонок:

- две mutating операции для одного `projectId` не выполняются одновременно;
- вторая операция получает conflict вместо конкурентного изменения состояния.

## Команды разработки

Основные команды из [package.json](../package.json):

- `npm run check`
- `npm run test`
- `npm run test:e2e`
- `npm run build`
- `npm run dev:stdio`
- `npm run dev:http`

## Эксплуатационная модель

### Локальный запуск

Проект ориентирован на локальный запуск разработчиком:

- с `.env`;
- с локальным `data/`;
- с локальным cache директорией моделей;
- с доступом к локальной файловой системе для индексации.

### Docker режим

Через `docker compose` можно поднять:

- `chroma`
- `rag-server`

Текущий репозиторий может быть примонтирован в контейнер как индексируемый проект.

## Наблюдаемость

Логирование основано на `pino`. Сейчас логируется:

- успешный запуск transport;
- fatal startup error;
- unhandled HTTP server error.

Это минимально достаточный слой, но пока без:

- structured metrics;
- request tracing;
- latency dashboards;
- отдельного event log для индексации.

## Текущие эксплуатационные риски

1. Первый прогон для проекта остаётся полным и дорогим.
2. Incremental indexing всё ещё сканирует файловую систему целиком и перечитывает файлы для пересчёта `contentHash`.
3. Отсутствует auth для mutating HTTP routes.
4. Текущий browser e2e покрывает только smoke-flow, без широкой матрицы ошибок и edge-cases.
5. Lock защищает от гонок, но пока не даёт очереди задач и user-visible job status.

## Практические направления развития

### Ближайшие

- расширить Playwright smoke негативными сценариями и ошибками API;
- показать пользователю ошибки индексации по файлам, а не только итоговый status.

### Среднесрочные

- добавить auth/token protection для mutating API;
- сделать более дешёвый, но корректный file fingerprint pipeline, если понадобится снова снижать IO без возврата stale-index риска.
- добавить per-project job queue и историю индексаций поверх уже существующего lock-механизма.

### Долгосрочные

- перейти на более формальный HTTP framework или router abstraction;
- выделить отдельный frontend build, если UI станет сложнее;
- добавить телеметрию и профилирование retrieval latency.
