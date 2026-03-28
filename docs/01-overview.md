# Architecture Overview

## Назначение проекта

Проект реализует локальный MCP/RAG-сервер на Node.js и TypeScript для работы с несколькими кодовыми базами. Он индексирует код и документацию, хранит чанки в ChromaDB, а затем отдаёт результаты через:

- MCP tools для агентных клиентов;
- Streamable HTTP transport;
- встроенный Web UI для browser-сценариев.

## Верхнеуровневая схема

Система состоит из пяти крупных слоёв:

1. Runtime bootstrap.
2. Transport layer.
3. Browser API и Web UI.
4. RAG services.
5. Persistence и external dependencies.

Поток данных в HTTP-режиме выглядит так:

1. Процесс стартует через [src/index.ts](../src/index.ts).
2. Загружается конфиг, инициализируются сервисы и зависимости.
3. HTTP server принимает:
   `GET /ui`, `GET/POST/PUT/DELETE /api/projects/*`, `POST /mcp`.
4. Browser API работает поверх тех же сервисов, что и MCP tools.
5. Индексация сканирует файловую систему, режет файлы на чанки и пересобирает коллекцию Chroma.
6. Поиск делает retrieval в Chroma, затем rerank локальной моделью, затем при необходимости собирает итоговый контекст.

## Основные сущности домена

Ключевые типы определены в [src/domain.ts](../src/domain.ts):

- `ProjectRegistration` — запись о зарегистрированном проекте.
- `IndexRunStats` — метрики запуска индексации.
- `IndexedChunk` — единица хранения в индексе.
- `SearchHit` — результат retrieval + rerank.
- `ContextBundle` — готовый упакованный контекст для агента.
- `ProjectIndexSummary` — состояние коллекции и preview чанков для UI.

## Архитектурные решения

### 1. Один набор сервисов для всех интерфейсов

И MCP tools, и browser API используют одни и те же зависимости:

- `ProjectRegistry`
- `ProjectIndexStateCache`
- `ProjectOperationLock`
- `IndexingService`
- `ContextBuilderService`
- `ChromaProjectStore`

Это снижает риск расхождения между поведением MCP и Web UI.

### 2. Изоляция индекса по проектам

Каждый проект хранится в отдельной коллекции Chroma вида `rag_project_<projectId>`. Такое решение упрощает:

- полную реиндексацию;
- удаление проекта;
- инспекцию состояния по одному project id;
- устранение случайного смешивания данных разных репозиториев.

Отдельно от Chroma хранится persisted file state проекта. Он не участвует в retrieval, но позволяет быстро решать, какие файлы действительно изменились с прошлого запуска.

В persisted state теперь хранятся `mtimeMs`, `size`, `contentHash`, `chunkIds` и `chunkHashes` по каждому исходному файлу. За счёт этого incremental reindex умеет удалять и заменять старые чанки, не читая всю Chroma collection целиком, и точнее определяет реальные изменения файла.

Отдельно от retrieval-path также существует `ProjectOperationLock`, который не даёт одновременно выполнить несколько mutating операций для одного и того же проекта. Это защищает от гонок между `reindex`, `delete`, `register/update`.

### 3. Browser API поверх обычного JSON

Web UI не работает напрямую по MCP JSON-RPC. Вместо этого сервер даёт отдельный browser-friendly API:

- проще писать UI без явного session management;
- проще валидировать и тестировать CRUD для проектов;
- проще развивать админские операции вроде `reindex` и `delete`.

### 4. Локальные модели вместо внешнего embedding API

Для эмбеддингов и rerank используется `@xenova/transformers`, а кэш моделей хранится локально. Это поддерживает локальный/внутренний и offline-friendly сценарий.

## Сильные стороны текущей архитектуры

- Малое число абстракций, легко держать в голове.
- Один bootstrap для `stdio` и `http`.
- Чёткое разделение project registry, indexing, retrieval и transport.
- Браузерный интерфейс строится на тех же бизнес-сервисах, что и MCP.

## Текущие ограничения

- HTTP слой остаётся достаточно ручным и не использует полноценный web framework, хотя зависимость `fastify` уже есть.
- Политика безопасности пока рассчитана на локальный/внутренний сценарий без auth.
- Файловая система всё ещё сканируется целиком на каждый `reindex`.
- Fingerprint стал надёжнее за счёт `contentHash`, но это всё ещё file-level эвристика, а не семантическое сравнение структуры кода.
- Web UI остаётся intentionally lightweight и покрыт только одним smoke-level e2e сценарием, а не широкой матрицей UI-регрессий.
