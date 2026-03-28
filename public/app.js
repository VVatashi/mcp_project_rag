const projectsList = document.getElementById("projects-list");
const refreshButton = document.getElementById("refresh-projects");
const createProjectForm = document.getElementById("create-project-form");
const createProjectIdInput = document.getElementById("create-project-id");
const createProjectRootInput = document.getElementById("create-project-root");
const createProjectDescriptionInput = document.getElementById("create-project-description");
const createProjectExcludePathsInput = document.getElementById("create-project-exclude-paths");
const createProjectExcludeExtensionsInput = document.getElementById("create-project-exclude-extensions");
const createProjectMaxFileSizeInput = document.getElementById("create-project-max-file-size");
const createProjectButton = document.getElementById("create-project-button");
const createProjectStatus = document.getElementById("create-project-status");
const projectTitle = document.getElementById("project-title");
const projectSubtitle = document.getElementById("project-subtitle");
const chunkCount = document.getElementById("chunk-count");
const projectDetails = document.getElementById("project-details");
const indexStatus = document.getElementById("index-status");
const chunksList = document.getElementById("chunks-list");
const chunkCaption = document.getElementById("chunk-caption");
const projectForm = document.getElementById("project-form");
const projectIdInput = document.getElementById("project-id-input");
const projectRootInput = document.getElementById("project-root-input");
const projectDescriptionInput = document.getElementById("project-description-input");
const projectExcludePathsInput = document.getElementById("project-exclude-paths-input");
const projectExcludeExtensionsInput = document.getElementById("project-exclude-extensions-input");
const projectMaxFileSizeInput = document.getElementById("project-max-file-size-input");
const saveProjectButton = document.getElementById("save-project");
const reindexProjectButton = document.getElementById("reindex-project");
const deleteProjectButton = document.getElementById("delete-project");
const actionFeedback = document.getElementById("action-feedback");
const queryForm = document.getElementById("query-form");
const queryMode = document.getElementById("query-mode");
const queryInput = document.getElementById("query-input");
const queryLimit = document.getElementById("query-limit");
const submitQueryButton = document.getElementById("submit-query");
const queryStatus = document.getElementById("query-status");
const queryResult = document.getElementById("query-result");
const projectItemTemplate = document.getElementById("project-item-template");
const chunkCardTemplate = document.getElementById("chunk-card-template");
const searchHitTemplate = document.getElementById("search-hit-template");

let selectedProjectId = null;
let projectsCache = [];

function parseMultilineList(value) {
  return value
    .split(/\r?\n|,/u)
    .map((item) => item.trim())
    .filter(Boolean);
}

function formatMultilineList(values) {
  return (values || []).join("\n");
}

function setActionFeedback(message, tone = "neutral") {
  actionFeedback.textContent = message;
  actionFeedback.className = `feedback-banner is-${tone}`;
}

async function fetchJson(url) {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`Request failed with status ${response.status}`);
  }
  return response.json();
}

async function sendJson(url, body) {
  return sendRequest(url, { method: "POST", body });
}

async function sendRequest(url, init = {}) {
  const response = await fetch(url, {
    method: init.method || "POST",
    headers: {
      "content-type": "application/json",
      accept: "application/json",
      ...(init.headers || {}),
    },
    body: init.body === undefined ? undefined : JSON.stringify(init.body),
  });

  const payload = await response.json().catch(() => ({}));
  if (!response.ok) {
    throw new Error(payload.error || `Request failed with status ${response.status}`);
  }

  return payload;
}

function renderProjects(projects) {
  projectsList.innerHTML = "";

  if (projects.length === 0) {
    projectsList.innerHTML = '<div class="empty-state">No projects registered yet.</div>';
    return;
  }

  for (const project of projects) {
    const fragment = projectItemTemplate.content.cloneNode(true);
    const button = fragment.querySelector(".project-item");
    const name = fragment.querySelector(".project-name");
    const meta = fragment.querySelector(".project-meta");
    const alert = fragment.querySelector(".project-alert");

    name.textContent = project.id;
    meta.textContent = project.index
      ? `${project.index.chunkCount} chunks • ${project.rootPath}`
      : `index unavailable • ${project.rootPath}`;
    alert.hidden = !project.indexError;
    if (project.indexError) {
      const observedAt = project.indexErrorAt
        ? new Date(project.indexErrorAt).toLocaleString()
        : "unknown time";
      alert.textContent = "Index issue";
      alert.title = `${project.indexError} (${observedAt})`;
      button.classList.add("has-warning");
    }

    if (project.id === selectedProjectId) {
      button.classList.add("is-active");
    }

    button.addEventListener("click", () => {
      selectedProjectId = project.id;
      renderProjects(projectsCache);
      loadProject(project.id);
    });

    projectsList.appendChild(fragment);
  }
}

function setCreateProjectPending(isPending, label) {
  createProjectIdInput.disabled = isPending;
  createProjectRootInput.disabled = isPending;
  createProjectDescriptionInput.disabled = isPending;
  createProjectExcludePathsInput.disabled = isPending;
  createProjectExcludeExtensionsInput.disabled = isPending;
  createProjectMaxFileSizeInput.disabled = isPending;
  createProjectButton.disabled = isPending;
  createProjectStatus.textContent = label;
}

function renderProjectDetails(project, index, indexError, indexErrorAt, diagnostics) {
  projectTitle.textContent = project.id;
  projectSubtitle.textContent = project.description || "Registered local project";
  chunkCount.textContent = String(index?.chunkCount ?? 0);
  projectIdInput.value = project.id;
  projectRootInput.value = project.rootPath;
  projectDescriptionInput.value = project.description || "";
  projectExcludePathsInput.value = formatMultilineList(project.excludePaths);
  projectExcludeExtensionsInput.value = formatMultilineList(project.excludeExtensions);
  projectMaxFileSizeInput.value = String(project.maxFileSizeBytes);

  const detailEntries = [
    ["Path", project.rootPath],
    ["Updated", project.updatedAt],
    ["Indexed", project.lastIndexedAt || "Not indexed yet"],
    ["Collection", index?.collectionName || "Unavailable"],
    ["Excluded paths", project.excludePaths.length ? String(project.excludePaths.length) : "0"],
    [
      "Excluded extensions",
      project.excludeExtensions.length ? project.excludeExtensions.join(", ") : "None",
    ],
    ["Max file size", `${project.maxFileSizeBytes} bytes`],
  ];

  projectDetails.innerHTML = detailEntries
    .map(([label, value]) => `<div><dt>${label}</dt><dd>${value}</dd></div>`)
    .join("");

  const lastIndexStats = project.lastIndexStats;
  indexStatus.innerHTML = [
    index
      ? `<p><strong>${index.chunkCount}</strong> chunks currently stored in Chroma.</p>`
      : '<p class="error-state">Index summary is currently unavailable.</p>',
    index
      ? `<p class="caption">Showing ${index.chunks.length} preview chunks from the current collection.</p>`
      : "",
    indexError
      ? `<p class="status-warning">${indexError}${indexErrorAt ? ` • observed ${new Date(indexErrorAt).toLocaleString()}` : ""}</p>`
      : "",
    diagnostics?.reindex
      ? `<p class="caption error-state">Last reindex failure: ${diagnostics.reindex.message} • ${new Date(diagnostics.reindex.observedAt).toLocaleString()}</p>`
      : "",
    lastIndexStats
      ? `<p class="caption">Last run: ${lastIndexStats.fileCount} files, ${lastIndexStats.chunkCount} chunks, ${lastIndexStats.durationMs} ms.</p>`
      : '<p class="caption">This project has not been indexed yet.</p>',
  ].join("");
}

function setProjectActionsPending(isPending, label) {
  const disabled = isPending || !selectedProjectId;
  projectRootInput.disabled = disabled;
  projectDescriptionInput.disabled = disabled;
  projectExcludePathsInput.disabled = disabled;
  projectExcludeExtensionsInput.disabled = disabled;
  projectMaxFileSizeInput.disabled = disabled;
  saveProjectButton.disabled = disabled;
  reindexProjectButton.disabled = disabled;
  deleteProjectButton.disabled = disabled;
  projectIdInput.disabled = true;

  if (label) {
    indexStatus.innerHTML = `<p>${label}</p>`;
  }
}

function renderChunks(chunks) {
  chunksList.innerHTML = "";
  chunkCaption.textContent = `${chunks.length} chunk previews`;

  if (chunks.length === 0) {
    chunksList.innerHTML =
      '<div class="empty-state">No indexed chunks yet. Run <code>index.run</code> for this project first.</div>';
    return;
  }

  for (const chunk of chunks) {
    const fragment = chunkCardTemplate.content.cloneNode(true);
    fragment.querySelector(".chunk-source").textContent = chunk.sourcePath;
    fragment.querySelector(".chunk-meta").textContent =
      chunk.startLine && chunk.endLine
        ? `${chunk.language} • lines ${chunk.startLine}-${chunk.endLine} • chunk #${chunk.chunkIndex}`
        : `${chunk.language} • chunk #${chunk.chunkIndex}`;
    fragment.querySelector(".chunk-kind").textContent = chunk.kind;
    fragment.querySelector(".chunk-text").textContent = chunk.text;
    chunksList.appendChild(fragment);
  }
}

function setQueryPending(isPending, label) {
  submitQueryButton.disabled = isPending || !selectedProjectId;
  queryInput.disabled = isPending || !selectedProjectId;
  queryMode.disabled = isPending || !selectedProjectId;
  queryLimit.disabled = isPending || !selectedProjectId || queryMode.value === "context";
  queryStatus.textContent = label;
}

function renderSearchHits(projectId, query, hits) {
  queryResult.innerHTML = "";

  const summary = document.createElement("div");
  summary.className = "result-summary";
  summary.innerHTML = `<p><strong>${hits.length}</strong> hits for <code>${query}</code> in <code>${projectId}</code>.</p>`;
  queryResult.appendChild(summary);

  if (hits.length === 0) {
    queryResult.innerHTML +=
      '<div class="empty-state">No relevant hits were found. Try a broader query or rebuild the index.</div>';
    return;
  }

  for (const hit of hits) {
    const fragment = searchHitTemplate.content.cloneNode(true);
    fragment.querySelector(".chunk-source").textContent = hit.sourcePath;
    fragment.querySelector(".chunk-meta").textContent =
      hit.startLine && hit.endLine
        ? `${hit.language} • score ${hit.finalScore.toFixed(3)} • lines ${hit.startLine}-${hit.endLine}`
        : `${hit.language} • score ${hit.finalScore.toFixed(3)}`;
    fragment.querySelector(".chunk-kind").textContent = hit.kind;
    fragment.querySelector(".chunk-text").textContent = hit.text;
    queryResult.appendChild(fragment);
  }
}

function renderContextBundle(bundle) {
  queryResult.innerHTML = `
    <div class="result-summary">
      <p><strong>Context bundle</strong> for <code>${bundle.projectId}</code>.</p>
      <p class="caption">${bundle.summary}</p>
    </div>
    <article class="chunk-card result-card">
      <header class="chunk-header">
        <div>
          <h3 class="chunk-source">context.build</h3>
          <p class="chunk-meta">${bundle.normalizedQuery}</p>
        </div>
        <span class="chunk-kind">context</span>
      </header>
      <pre class="chunk-text"></pre>
    </article>
  `;

  queryResult.querySelector(".chunk-text").textContent = bundle.contextText;
}

function resetQueryResult(message) {
  queryResult.innerHTML = `<div class="empty-state">${message}</div>`;
}

function resetProjectDetails(message) {
  projectTitle.textContent = "Select a project";
  projectSubtitle.textContent = message;
  chunkCount.textContent = "0";
  projectIdInput.value = "";
  projectRootInput.value = "";
  projectDescriptionInput.value = "";
  projectExcludePathsInput.value = "";
  projectExcludeExtensionsInput.value = "";
  projectMaxFileSizeInput.value = "8388608";
  projectDetails.innerHTML = `
    <div><dt>Path</dt><dd>-</dd></div>
    <div><dt>Updated</dt><dd>-</dd></div>
    <div><dt>Indexed</dt><dd>-</dd></div>
    <div><dt>Collection</dt><dd>-</dd></div>
    <div><dt>Excluded paths</dt><dd>-</dd></div>
    <div><dt>Excluded extensions</dt><dd>-</dd></div>
    <div><dt>Max file size</dt><dd>-</dd></div>
  `;
  indexStatus.innerHTML = "<p>No project selected yet.</p>";
  chunksList.innerHTML = "";
  chunkCaption.textContent = "Nothing loaded";
  setActionFeedback("Select a project to manage it from the browser.");
}

async function loadProjects() {
  try {
    const payload = await fetchJson("/api/projects");
    projectsCache = payload.projects || [];
    if (selectedProjectId && !projectsCache.some((project) => project.id === selectedProjectId)) {
      selectedProjectId = projectsCache[0]?.id || null;
    }
    if (!selectedProjectId && projectsCache[0]) {
      selectedProjectId = projectsCache[0].id;
    }
    renderProjects(projectsCache);

    if (selectedProjectId) {
      await loadProject(selectedProjectId);
    } else {
      resetProjectDetails("No projects registered yet.");
      setProjectActionsPending(false);
      setQueryPending(false, "Select a project to start");
      resetQueryResult("Choose a project and submit a query to see results here.");
    }
  } catch (error) {
    projectsList.innerHTML = `<div class="empty-state error-state">${error.message}</div>`;
  }
}

async function loadProject(projectId) {
  try {
    const payload = await fetchJson(`/api/projects/${encodeURIComponent(projectId)}`);
    renderProjectDetails(
      payload.project,
      payload.index,
      payload.indexError,
      payload.indexErrorAt,
      payload.diagnostics,
    );
    renderChunks(payload.index?.chunks || []);
    setProjectActionsPending(false);
    setQueryPending(false, `Ready for ${projectId}`);
    setActionFeedback(`Project ${projectId} is ready. You can edit settings or reindex it.`, "neutral");
  } catch (error) {
    resetProjectDetails(error.message);
    indexStatus.innerHTML = `<p class="error-state">${error.message}</p>`;
    setProjectActionsPending(false);
    setQueryPending(false, "Project is unavailable");
    resetQueryResult(error.message);
    setActionFeedback(error.message, "error");
  }
}

refreshButton.addEventListener("click", () => {
  loadProjects();
});

createProjectForm.addEventListener("submit", async (event) => {
  event.preventDefault();

  const id = createProjectIdInput.value.trim();
  const rootPath = createProjectRootInput.value.trim();
  const description = createProjectDescriptionInput.value.trim();
  const excludePaths = parseMultilineList(createProjectExcludePathsInput.value);
  const excludeExtensions = parseMultilineList(createProjectExcludeExtensionsInput.value);
  const maxFileSizeBytes = Number.parseInt(createProjectMaxFileSizeInput.value, 10);

  if (id.length < 2) {
    createProjectStatus.textContent = "Project ID must contain at least 2 characters.";
    setActionFeedback("Project ID must contain at least 2 characters.", "error");
    return;
  }

  if (!rootPath) {
    createProjectStatus.textContent = "Project rootPath is required.";
    setActionFeedback("Project rootPath is required.", "error");
    return;
  }

  if (!Number.isFinite(maxFileSizeBytes) || maxFileSizeBytes < 1) {
    createProjectStatus.textContent = "Max file size must be a positive integer.";
    setActionFeedback("Max file size must be a positive integer.", "error");
    return;
  }

  setCreateProjectPending(true, "Creating project...");

  try {
    const payload = await sendRequest("/api/projects", {
      method: "POST",
      body: {
        id,
        rootPath,
        description,
        excludePaths,
        excludeExtensions,
        maxFileSizeBytes,
      },
    });

    selectedProjectId = payload.project.id;
    createProjectForm.reset();
    createProjectMaxFileSizeInput.value = "8388608";
    setCreateProjectPending(false, `Project ${payload.project.id} created.`);
    setActionFeedback(`Project ${payload.project.id} created successfully.`, "success");
    await loadProjects();
  } catch (error) {
    setCreateProjectPending(false, error.message);
    setActionFeedback(error.message, "error");
  }
});

projectForm.addEventListener("submit", async (event) => {
  event.preventDefault();

  if (!selectedProjectId) {
    return;
  }

  const rootPath = projectRootInput.value.trim();
  const description = projectDescriptionInput.value.trim();
  const excludePaths = parseMultilineList(projectExcludePathsInput.value);
  const excludeExtensions = parseMultilineList(projectExcludeExtensionsInput.value);
  const maxFileSizeBytes = Number.parseInt(projectMaxFileSizeInput.value, 10);

  if (!rootPath) {
    indexStatus.innerHTML = '<p class="error-state">Project rootPath is required.</p>';
    setActionFeedback("Project rootPath is required.", "error");
    return;
  }

  if (!Number.isFinite(maxFileSizeBytes) || maxFileSizeBytes < 1) {
    indexStatus.innerHTML = '<p class="error-state">Max file size must be a positive integer.</p>';
    setActionFeedback("Max file size must be a positive integer.", "error");
    return;
  }

  setProjectActionsPending(true, "Saving project...");

  try {
    await sendRequest(`/api/projects/${encodeURIComponent(selectedProjectId)}`, {
      method: "PUT",
      body: {
        rootPath,
        description,
        excludePaths,
        excludeExtensions,
        maxFileSizeBytes,
      },
    });
    await loadProjects();
    indexStatus.innerHTML = "<p>Project settings updated.</p>";
    setActionFeedback(`Project ${selectedProjectId} updated successfully.`, "success");
  } catch (error) {
    setProjectActionsPending(false, `Save failed: ${error.message}`);
    indexStatus.innerHTML = `<p class="error-state">${error.message}</p>`;
    setActionFeedback(error.message, "error");
  }
});

reindexProjectButton.addEventListener("click", async () => {
  if (!selectedProjectId) {
    return;
  }

  setProjectActionsPending(true, "Reindexing project...");
  setQueryPending(true, "Reindexing project...");

  try {
    const payload = await sendJson(
      `/api/projects/${encodeURIComponent(selectedProjectId)}/reindex`,
      {},
    );
    renderProjectDetails(
      payload.project,
      payload.index,
      payload.indexError,
      payload.indexErrorAt,
      payload.diagnostics,
    );
    renderChunks(payload.index?.chunks || []);
    indexStatus.innerHTML = [
      `<p><strong>${payload.stats.chunkCount}</strong> chunks stored after ${payload.stats.mode || "full"} indexing.</p>`,
      `<p class="caption">Changed files: ${payload.stats.changedFileCount ?? payload.stats.fileCount}; removed: ${payload.stats.removedFileCount ?? 0}; unchanged: ${payload.stats.unchangedFileCount ?? 0}.</p>`,
      `<p class="caption">Finished in ${payload.stats.durationMs} ms.</p>`,
      payload.indexError ? `<p class="caption error-state">${payload.indexError}</p>` : "",
    ].join("");
    await loadProjects();
    setQueryPending(false, `Ready for ${selectedProjectId}`);
    setActionFeedback(
      `Reindex completed for ${selectedProjectId}: ${payload.stats.changedFileCount ?? payload.stats.fileCount} changed, ${payload.stats.removedFileCount ?? 0} removed, ${payload.stats.unchangedFileCount ?? 0} unchanged.`,
      "success",
    );
  } catch (error) {
    indexStatus.innerHTML = `<p class="error-state">${error.message}</p>`;
    setProjectActionsPending(false);
    setQueryPending(false, "Reindex failed");
    setActionFeedback(error.message, "error");
  }
});

deleteProjectButton.addEventListener("click", async () => {
  if (!selectedProjectId) {
    return;
  }

  const shouldDelete = window.confirm(
    `Delete project "${selectedProjectId}" and remove its indexed collection?`,
  );
  if (!shouldDelete) {
    setActionFeedback("Project deletion cancelled.", "neutral");
    return;
  }

  setProjectActionsPending(true, "Deleting project...");
  setQueryPending(true, "Deleting project...");

  try {
    await sendRequest(`/api/projects/${encodeURIComponent(selectedProjectId)}`, {
      method: "DELETE",
    });
    setActionFeedback(`Project ${selectedProjectId} deleted.`, "success");
    selectedProjectId = null;
    resetQueryResult("Choose a project and submit a query to see results here.");
    await loadProjects();
  } catch (error) {
    indexStatus.innerHTML = `<p class="error-state">${error.message}</p>`;
    setProjectActionsPending(false);
    setQueryPending(false, "Delete failed");
    setActionFeedback(error.message, "error");
  }
});

queryMode.addEventListener("change", () => {
  queryLimit.disabled = queryMode.value === "context" || !selectedProjectId;
});

queryForm.addEventListener("submit", async (event) => {
  event.preventDefault();

  if (!selectedProjectId) {
    resetQueryResult("Choose a project before sending a query.");
    return;
  }

  const query = queryInput.value.trim();
  if (query.length < 3) {
    resetQueryResult("Query must contain at least 3 characters.");
    return;
  }

  const mode = queryMode.value;
  const limit = Number.parseInt(queryLimit.value, 10) || 6;

  setQueryPending(true, mode === "context" ? "Building context..." : "Searching...");

  try {
    if (mode === "context") {
      const payload = await sendJson(
        `/api/projects/${encodeURIComponent(selectedProjectId)}/context`,
        { query },
      );
      renderContextBundle(payload);
      setQueryPending(false, `Context ready for ${selectedProjectId}`);
      setActionFeedback(`Context bundle built for ${selectedProjectId}.`, "success");
      return;
    }

    const payload = await sendJson(
      `/api/projects/${encodeURIComponent(selectedProjectId)}/search`,
      { query, limit },
    );
    renderSearchHits(payload.projectId, payload.query, payload.hits || []);
    setQueryPending(false, `Found ${payload.hits?.length ?? 0} hits`);
    setActionFeedback(
      `Search completed for ${payload.projectId}: ${payload.hits?.length ?? 0} hits.`,
      "success",
    );
  } catch (error) {
    queryResult.innerHTML = `<div class="empty-state error-state">${error.message}</div>`;
    setQueryPending(false, "Request failed");
    setActionFeedback(error.message, "error");
  }
});

setCreateProjectPending(false, "Create a project registration from the browser.");
setProjectActionsPending(false);
setQueryPending(false, "Select a project to start");
setActionFeedback("Select a project or create a new one to begin.");
loadProjects();
