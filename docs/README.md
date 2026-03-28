# Project Docs

Этот каталог фиксирует текущее состояние архитектуры проекта `mcp-project-rag-server`.

## Состав

- [01-overview.md](./01-overview.md) — назначение системы, верхнеуровневая схема и ключевые сущности.
- [02-runtime-and-backend.md](./02-runtime-and-backend.md) — запуск, конфигурация, композиция зависимостей, MCP и HTTP слой.
- [03-rag-pipeline.md](./03-rag-pipeline.md) — реестр проектов, discovery, chunking, embeddings, Chroma, rerank и context building.
- [04-web-ui-and-browser-api.md](./04-web-ui-and-browser-api.md) — структура встроенного Web UI и browser-friendly HTTP API.
- [05-testing-and-operations.md](./05-testing-and-operations.md) — тесты, эксплуатация, ограничения и направления развития.

## Зачем это нужно

Документы описывают не только то, "что есть", но и "почему так сделано сейчас":

- как сервер переключается между `stdio` и `http`;
- почему проект хранит индекс по отдельным Chroma collections;
- как Web UI использует отдельный browser API, а не MCP JSON-RPC напрямую;
- где проходят границы между runtime, transport и RAG-сервисами.

## Границы актуальности

Документация отражает состояние репозитория на текущем этапе разработки, включая:

- встроенный Web UI для создания, редактирования, удаления и реиндексации проектов;
- browser API поверх HTTP transport;
- persisted `mtime` cache для инкрементальной реиндексации;
- двухэтапный retrieval pipeline: Chroma retrieval + локальный rerank;
- модульный HTTP слой после выноса `browser-api`, `http-utils`, `static-files`.

## Правило по путям

- Документация не должна раскрывать абсолютные локальные пути текущего окружения.
- Для ссылок и примеров используются относительные пути репозитория или нейтральные placeholders.
