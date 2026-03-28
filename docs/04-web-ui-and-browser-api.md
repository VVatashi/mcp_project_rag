# Web UI And Browser API

## Назначение Web UI

Встроенный UI нужен как лёгкая панель управления сервером без MCP-клиента. Он даёт browser-сценарий для:

- просмотра зарегистрированных проектов;
- создания проекта;
- редактирования `rootPath` и `description`;
- удаления проекта;
- запуска реиндексации;
- semantic search;
- `context.build` из браузера;
- просмотра preview чанков.

## Структура фронтенда

UI живёт в каталоге `public/`:

- [public/index.html](../public/index.html)
- [public/app.js](../public/app.js)
- [public/styles.css](../public/styles.css)

### Почему без framework

Текущий UI написан на ванильном JS.

Плюсы:

- нулевая сборка фронтенда;
- легко раздавать как статические файлы;
- минимальный runtime overhead.

Минусы:

- логика состояния уже становится заметно плотной;
- сложнее масштабировать экран и повторно использовать UI-примитивы;
- отсутствует компонентная модель.

## Структура экрана

Основные блоки страницы:

1. Sidebar с проектами и формой создания.
2. Hero блок с текущим проектом.
3. Project Details.
4. Project Actions.
5. Query Playground.
6. Indexed Chunks.

## Project management flow

### Создание проекта

Форма создания отправляет `POST /api/projects`.

После успеха:

- новый проект становится выбранным;
- список проектов перезагружается;
- UI показывает feedback message.

### Редактирование проекта

Редактируется только:

- `rootPath`
- `description`

`project id` в UI readonly. Это осознанное решение:

- id используется как ключ коллекции в Chroma;
- переименование id потребовало бы отдельного migration flow;
- обычный edit path/description — более частый и безопасный сценарий.

### Удаление проекта

Удаление подтверждается через `window.confirm`.

Операция делает:

1. `DELETE /api/projects/{projectId}`
2. очистку коллекции Chroma
3. удаление проекта из реестра
4. обновление экрана

### Реиндексация

Запрос отправляется на `POST /api/projects/{projectId}/reindex`.

После ответа UI:

- обновляет project details;
- обновляет summary index status;
- перерисовывает preview чанков;
- показывает режим индексации и счётчики `changed / removed / unchanged`.

## Query playground

Query playground поддерживает два режима:

- `search`
- `context`

### `search`

Вызывает `POST /api/projects/{projectId}/search` и рисует список `SearchHit`.

### `context`

Вызывает `POST /api/projects/{projectId}/context` и показывает итоговый `contextText`.

### UX решения

Текущий UI уже содержит:

- дизейбл контролов на время запроса;
- баннер успеха или ошибки;
- summary по количеству hits;
- summary по последней индексации проекта.
- warning в списке проектов и в карточке проекта, если `inspectProject` временно недоступен.
- timestamp последней inspection/reindex ошибки через diagnostics-поля browser API.

## Browser API design notes

Browser API находится в [src/http/browser-api.ts](../src/http/browser-api.ts).

### Почему это не MCP JSON-RPC

Прямой вызов MCP из браузера усложнил бы:

- lifecycle session id;
- форматирование JSON-RPC;
- UX ошибок и подтверждений;
- простые CRUD сценарии для проектов.

Поэтому browser API специально сделан более "админским" и простым.

## Безопасность и ограничения

Текущие улучшения:

- блокируется path traversal при раздаче `/ui/*`;
- invalid JSON даёт `400`;
- destructive operations доступны только через тот же HTTP сервер и пока рассчитаны на локальный/внутренний use case.

Что пока остаётся ограничением:

- нет auth;
- нет CSRF/ACL модели;
- нет полноценной audit trail по действиям через UI.
