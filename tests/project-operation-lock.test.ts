import { describe, expect, it } from "vitest";
import {
  ProjectOperationConflictError,
  ProjectOperationLock,
} from "../src/services/project-operation-lock.js";

describe("ProjectOperationLock", () => {
  it("rejects overlapping mutating operations for the same project", async () => {
    const lock = new ProjectOperationLock();

    let releaseFirst = () => {};
    const first = lock.runExclusive("demo", async () => {
      await new Promise<void>((resolve) => {
        releaseFirst = resolve;
      });
      return "first";
    });

    await expect(lock.runExclusive("demo", async () => "second")).rejects.toBeInstanceOf(
      ProjectOperationConflictError,
    );

    releaseFirst();
    await expect(first).resolves.toBe("first");
  });
});
