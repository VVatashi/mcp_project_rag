export type IndexDiagnosticSource = "inspect" | "reindex";

export interface IndexDiagnosticEntry {
  source: IndexDiagnosticSource;
  message: string;
  observedAt: string;
}

export interface ProjectIndexDiagnostics {
  inspect?: IndexDiagnosticEntry;
  reindex?: IndexDiagnosticEntry;
}

export class IndexDiagnosticsService {
  private readonly diagnostics = new Map<string, ProjectIndexDiagnostics>();

  public get(projectId: string): ProjectIndexDiagnostics | undefined {
    return this.diagnostics.get(projectId);
  }

  public record(
    projectId: string,
    source: IndexDiagnosticSource,
    message: string,
    observedAt = new Date().toISOString(),
  ): IndexDiagnosticEntry {
    const next = {
      ...(this.diagnostics.get(projectId) ?? {}),
      [source]: {
        source,
        message,
        observedAt,
      },
    } satisfies ProjectIndexDiagnostics;

    this.diagnostics.set(projectId, next);
    return next[source]!;
  }

  public clear(projectId: string, source?: IndexDiagnosticSource): void {
    const current = this.diagnostics.get(projectId);
    if (!current) {
      return;
    }

    if (!source) {
      this.diagnostics.delete(projectId);
      return;
    }

    const next = { ...current };
    delete next[source];
    if (Object.keys(next).length === 0) {
      this.diagnostics.delete(projectId);
      return;
    }

    this.diagnostics.set(projectId, next);
  }
}
