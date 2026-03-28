# MCP Project RAG Server

Node.js/TypeScript MCP-сервер для локального RAG по нескольким проектам с хранением чанков в ChromaDB.

## Что уже есть

- MCP-сервер на официальном TypeScript SDK с поддержкой `stdio` и Streamable HTTP.
- Простой встроенный web UI для просмотра проектов и чанков индекса в обязательной тёмной теме.
- Реестр локальных проектов с явной регистрацией путей.
- Индексация кода и документации по чанкам.
- Persisted file state с `mtime`, `size`, `contentHash`, `chunkIds` и `chunkHashes` для инкрементальной реиндексации.
- Project-level lock для mutating операций, чтобы не смешивать параллельные `reindex/update/delete/register`.
- Atomic write для `projects.json` и `index-state/*.json`.
- Разделение данных по проектам через отдельные Chroma collections.
- Двухэтапный поиск: retrieval в Chroma + локальный neural rerank.
- Сборка контекста под задачу агента через MCP tool `context.build`.
- Python-референс сохранён в `legacy/python-reference`.

## Критический разбор требований

1. Нужен жизненный цикл индекса: полная переиндексация, обработка удалений и rename-файлов, а позже инкрементальные обновления.
2. Нужна строгая фильтрация файлов: без неё в индекс попадут `dist`, `coverage`, lock-файлы и сгенерированный код.
3. Нужна политика контекстного бюджета: RAG должен уметь не просто искать, а собирать компактный контекст под лимит.
4. Нужен lifecycle локальных моделей: кэш, прогрев, fallback и контроль памяти.
5. Нужны наблюдаемость и диагностика: статусы индексации, размеры коллекций, время поиска и ошибки файловой системы.
6. Нужны будущие ограничения безопасности: сейчас проект ориентирован на локальное/внутреннее использование, без auth и ACL.
7. Нужна дальнейшая оптимизация incremental indexing: теперь проект уже не читает всю collection на каждый `reindex`, но файловая система всё ещё сканируется целиком, а для гарантии корректности `contentHash` пересчитывается по содержимому файла на каждом incremental run.

## Архитектура

- `projects.register`: регистрирует локальный проект.
- `index.run`: сканирует проект, инкрементально переиндексирует только изменившиеся файлы и удаляет чанки для исчезнувших файлов. При первом запуске выполняется полная сборка индекса.
- `search.query`: делает семантический поиск с rerank.
- `context.build`: собирает готовый контекст для агента.

Ключевые слои:

- `ProjectRegistry` хранит метаданные проектов в `data/projects.json`.
- `ProjectIndexStateCache` хранит persisted состояние файлов и их chunk manifest в `data/index-state/<projectId>.json`.
- `ProjectOperationLock` сериализует mutating операции на уровне конкретного `projectId`.
- `FileDiscoveryService` обходит файловую систему с учётом `.gitignore`, project-level excludes, exclude paths/extensions и лимита размера файла.
- `ChunkingService` режет код по окнам строк, а документацию по абзацам.
- `TransformersEmbeddingFunction` строит локальные эмбеддинги через `@xenova/transformers`.
- `NeuralReranker` делает второй этап ранжирования.
- `ChromaProjectStore` изолирует проекты по коллекциям `rag_project_<projectId>`.
- `ContextBuilderService` собирает стандартизированный контекст для MCP-ответа.

## Быстрый старт

1. Скопируйте пример конфига и при необходимости настройте `.env`:

```bash
cp .env.example .env
```

2. Если хотите локальный запуск без Docker, установите зависимости:

```bash
npm install
```

3. Для запуска через Docker Compose:

```bash
docker compose up --build
```

Сервисы будут доступны так:

- ChromaDB: `http://127.0.0.1:8000`
- MCP server: `http://127.0.0.1:3123/mcp`
- Web UI: `http://127.0.0.1:3123/ui`

4. Для локального MCP-клиента без Docker:

```bash
npm run dev:stdio
```

5. Для локального Streamable HTTP без Docker:

```bash
$env:MCP_TRANSPORT="http"
npm run dev:http
```

## Docker Compose

В репозитории есть готовые:

- [docker-compose.yml](./docker-compose.yml)
- [Dockerfile](./Dockerfile)

Compose поднимает два сервиса:

- `chroma` с постоянным volume для векторной БД
- `rag-server` с Node.js MCP-сервером

По умолчанию текущий репозиторий монтируется в контейнер как `/workspace/current-project`. Это сделано специально: чтобы сервер внутри контейнера мог индексировать локальный код, проект должен быть примонтирован внутрь контейнера как bind mount.

Если нужно подключать другие локальные проекты, добавьте новые bind mounts в `rag-server.volumes`, например:

```yaml
volumes:
  - ./:/workspace/current-project:ro
  - /absolute/path/to/another-project:/workspace/another-project:ro
```

После этого регистрируйте проект в MCP уже по контейнерному пути, например `/workspace/another-project`.

## Конфигурация

Проект в текущем виде рассчитан прежде всего на локальный или внутренний контур разработки. Публиковать его как внешний публичный сервис без отдельного слоя аутентификации, авторизации и network hardening не предполагается.

Сервер автоматически читает переменные из `.env` в корне проекта. Переменные окружения процесса имеют приоритет над значениями из файла.

- `MCP_TRANSPORT=stdio|http`
- `MCP_HTTP_HOST=127.0.0.1`
- `MCP_HTTP_PORT=3123`
- `MCP_HTTP_PATH=/mcp`
- `MCP_DATA_DIR=./data`
- `CHROMA_URL=http://127.0.0.1:8000`
- `EMBEDDING_MODEL=Xenova/all-MiniLM-L6-v2`
- `RERANK_MODEL=Xenova/all-MiniLM-L6-v2`
- `MAX_CONTEXT_CHARS=12000`

Сервер также автоматически использует служебные директории внутри `MCP_DATA_DIR`:

- `projects.json` для реестра проектов
- `index-state/` для persisted file state (`mtime`, `size`, `contentHash`, `chunkIds`, `chunkHashes`)
- `models/` для локального кэша моделей

## Правило по путям

- Код, документация и примеры не должны содержать абсолютные локальные пути текущего окружения.
- В репозитории допускаются только относительные пути внутри проекта или нейтральные placeholders вроде `/absolute/path/to/project`.
- Нельзя публиковать пути вида домашнего каталога разработчика, рабочей папки машины или иного environment-specific location.

## Основные MCP tools

- `projects.list`
- `projects.register`
- `projects.unregister`
- `index.run`
- `search.query`
- `context.build`

## Web UI

В HTTP-режиме сервер также поднимает простой интерфейс:

- `GET /ui` — тёмная панель просмотра, создания, редактирования, удаления и реиндексации проектов
- `GET /api/projects` — список зарегистрированных проектов и краткая сводка по индексу
- `POST /api/projects` — регистрация нового проекта из браузера
- `GET /api/projects/{projectId}` — подробности проекта и preview чанков из текущего индекса
- если index inspection временно недоступен, UI показывает warning прямо в списке проектов и отдельный `indexError` в карточке проекта вместо подмены пустым индексом
- browser API также возвращает timestamp последней ошибки inspection/reindex через diagnostics-поля, чтобы UI мог показывать свежесть проблемы
- `PUT /api/projects/{projectId}` — редактирование `rootPath` и `description` проекта
- `DELETE /api/projects/{projectId}` — удаление проекта и его коллекции из индекса
- `POST /api/projects/{projectId}/reindex` — ручной запуск реиндексации проекта; после первого полного прогона используется инкрементальный режим с persisted file state и пересчётом `contentHash` для защиты от stale index
- `POST /api/projects/{projectId}/search` — семантический поиск из браузера, принимает `{ "query": string, "limit"?: number }`
- `POST /api/projects/{projectId}/context` — сборка `context.build` через browser UI, принимает `{ "query": string }`

## Проверка

```bash
npm run check
npm run test
npm run test:e2e
npm run build
```

Последний smoke-status:

- `GET /ui` отдаётся успешно;
- `POST /api/projects` и повторный `POST /api/projects/{projectId}/reindex` дают переход `full -> incremental`;
- второй `reindex` возвращает `unchangedFileCount=1` для неизменённого smoke-файла;
- MCP `initialize` и `tools/call search.query` успешно проходят поверх `/mcp`.
- `npm run test:e2e` прогоняет browser-level UI smoke через Playwright.
