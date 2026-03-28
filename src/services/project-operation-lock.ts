export class ProjectOperationConflictError extends Error {
  public constructor(projectId: string) {
    super(`Project ${projectId} is busy with another mutating operation`);
  }
}

export class ProjectOperationLock {
  private readonly activeProjects = new Set<string>();

  public async runExclusive<T>(
    projectId: string,
    task: () => Promise<T> | T,
  ): Promise<T> {
    if (this.activeProjects.has(projectId)) {
      throw new ProjectOperationConflictError(projectId);
    }

    this.activeProjects.add(projectId);
    try {
      return await task();
    } finally {
      this.activeProjects.delete(projectId);
    }
  }

  public isLocked(projectId: string): boolean {
    return this.activeProjects.has(projectId);
  }
}
