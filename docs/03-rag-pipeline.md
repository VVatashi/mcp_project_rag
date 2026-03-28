# RAG Pipeline

## Общая последовательность

RAG pipeline в проекте делится на два основных режима:

1. Индексация.
2. Поиск и сборка контекста.

## 1. Реестр проектов

Реестр реализован в [src/services/project-registry.ts](../src/services/project-registry.ts).

Что хранится в записи проекта:

- `id`
- `rootPath`
- `description`
- `includeGlobs`
- `excludeGlobs`
- `createdAt`
- `updatedAt`
- `lastIndexedAt`
- `lastIndexStats`

### Текущее решение

Реестр хранится в `data/projects.json`.

Плюсы:

- нулевая сложность развёртывания;
- легко дебажить вручную;
- удобно для локального single-user окружения.

Минусы:

- нет блокировки от конкурирующих процессов;
- нет истории изменений.

## 1.1. Persisted file state

Дополнительное persisted состояние индекса хранит [src/services/project-index-state-cache.ts](../src/services/project-index-state-cache.ts).

Этот cache содержит:

- `projectId`
- `rootPath`
- `updatedAt`
- словарь файлов по `relativePath`
- для каждого файла:
  `mtimeMs`, `size`, `contentHash`, `chunkIds`, `chunkHashes`

Назначение cache:

- не выполнять chunking для файлов, которые реально не менялись;
- быстро определять `changed / unchanged / removed` между запусками;
- удалять и заменять старые чанки по persisted manifest без чтения всей Chroma collection;
- сохранять инкрементальное состояние между рестартами процесса.

## 2. Discovery файлов

Сканирование файлов выполняет [src/services/file-discovery.ts](../src/services/file-discovery.ts).

Алгоритм:

1. Загружается `.gitignore` проекта.
2. Поверх него добавляются internal excludes из `ProjectRegistry`.
3. Рекурсивно обходится файловая система в `stat-only` режиме.
4. Для каждого файла собираются только `relativePath`, `mtimeMs` и `size`.
5. Оставляются только типы, распознаваемые через `guessFileKind`.
6. Содержимое файла читается на полном rebuild и на incremental run, чтобы каждый раз пересчитать актуальный `contentHash`.

### Важное решение

Фильтрация делается до chunking и embeddings. Discovery остаётся двухфазным: сначала обход по `stat`, затем чтение содержимого. На incremental run это чтение теперь выполняется для всех файлов, чтобы не пропускать изменения, которые сохранили тот же `mtime/size`.

## 3. Chunking

Chunking реализован в [src/services/chunker.ts](../src/services/chunker.ts).

Есть два режима:

- code files
- doc files

### Code chunking

Параметры:

- окно `80` строк
- overlap `20` строк

Почему так:

- сохраняется локальный контекст;
- уменьшается риск потерять сигнатуру рядом с реализацией;
- chunk ids остаются детерминированными.

### Doc chunking

Параметры:

- окно `1200` символов
- overlap `200` символов

Для markdown и docs сначала удаляется frontmatter через `gray-matter`, затем текст режется по абзацам.

## 4. Embeddings

Локальный embedding слой реализован в [src/services/embedder.ts](../src/services/embedder.ts).

Ключевые решения:

- используется `@xenova/transformers`;
- модели кэшируются в `modelCacheDir`;
- пространство по умолчанию — `cosine`;
- embedding extractor инициализируется лениво.

Это даёт простой offline-friendly сценарий без внешнего embedding провайдера.

## 5. Хранилище в Chroma

Работа с Chroma инкапсулирована в [src/services/chroma-store.ts](../src/services/chroma-store.ts).

### Основные операции

- `replaceProjectChunks`
- `queryProject`
- `deleteProject`
- `inspectProject`

### Стратегия индексации

При первом запуске проектная коллекция создаётся полной загрузкой всех чанков. При последующих запусках используется инкрементальная стратегия.

Алгоритм инкрементальной реиндексации:

1. Файловая система сканируется заново в `stat-only` режиме.
2. Загружается persisted state проекта.
3. Если state отсутствует, повреждён, относится к другому `rootPath` или ещё не содержит chunk manifest, выполняется полный rebuild.
4. Для каждого текущего файла читается содержимое и пересчитывается `contentHash`.
5. Если новый `contentHash` совпал со старым, файл считается `unchanged`, а `chunkIds/chunkHashes` переиспользуются из persisted state.
6. Если `contentHash` не совпал или файла раньше не было, файл считается реально changed.
7. Для реально changed файлов старые чанки удаляются по persisted `chunkIds`, новые чанки строятся заново и `upsert`-ятся.
8. Если файл исчез, его чанки тоже удаляются по persisted `chunkIds`.
9. После завершения индексации persisted state целиком обновляется уже с новым manifest.

Плюсы:

- embeddings и запись в Chroma выполняются только для реально изменившихся файлов;
- chunking тоже пропускается для файлов, чей `contentHash` не изменился;
- удалённые файлы корректно вычищаются из индекса;
- incremental path больше не требует `collection.get()` по всем чанкам проекта;
- полный rebuild остаётся как fallback для самого первого запуска.

Минусы:

- файловая система всё равно сканируется целиком;
- incremental run теперь тоже требует чтения файла для пересчёта `contentHash`, если нужна строгая защита от stale index;
- file-level fingerprint не даёт знания о семантической эквивалентности кода;
- fallback на полный rebuild всё ещё нужен при старом или несовместимом формате persisted state.

### Метаданные чанка

В Chroma сохраняются:

- `projectId`
- `sourcePath`
- `sourceName`
- `kind`
- `language`
- `chunkIndex`
- `startLine`
- `endLine`
- `hash`

Это позволяет в рантайме усиливать нейронный retrieval структурными подсказками.

## 6. Retrieval и rerank

### Retrieval

Первичный поиск идёт через `collection.query()` в Chroma.

Результаты конвертируются в `SearchHit` с полями:

- `retrievalScore`
- `distance`
- `text`
- файловые и языковые метаданные

### Rerank

Rerank реализован в [src/services/reranker.ts](../src/services/reranker.ts).

Финальный score складывается из:

- neural similarity;
- lexical overlap;
- phrase match;
- retrieval score;
- metadata boost по пути, имени файла, языку и типу чанка.

Это означает, что система не полагается только на embeddings. Она смешивает semantic и structural сигнал.

## 7. Diversification и context building

Сборка финального ответа идёт через [src/services/context-builder.ts](../src/services/context-builder.ts).

Этапы:

1. Нормализация query.
2. Поиск с повышенным `initialLimit`.
3. Rerank.
4. Diversification по файлам и соседним чанкам.
5. Сборка summary.
6. Обрезка по `maxContextChars`.

### Почему diversification важен

Без него топ выдачи легко забивается соседними chunk-окнами одного и того же файла. Текущий алгоритм пытается распределить ответы между разными source paths.

### Формат `context.build`

Итоговый `contextText` содержит:

- summary по проекту и intent;
- список top files;
- выбранные фрагменты с source location и score.

Этот формат пригоден и для MCP tool ответа, и для ручного использования в агентном prompt.
