import { z } from "zod";
import { HttpError } from "./http-error.js";

export const createProjectRequestSchema = z.object({
  id: z.string().trim().min(2, "Project id must contain at least 2 characters"),
  rootPath: z.string().trim().min(1, "Project rootPath is required"),
  description: z.string().trim().optional().transform((value) => value || undefined),
  includeGlobs: z.array(z.string().trim().min(1)).optional(),
  excludeGlobs: z.array(z.string().trim().min(1)).optional(),
  excludePaths: z.array(z.string().trim().min(1)).optional(),
  excludeExtensions: z.array(z.string().trim().min(1)).optional(),
  maxFileSizeBytes: z.number().int().positive().optional(),
});

export const updateProjectRequestSchema = z.object({
  rootPath: z.string().trim().min(1, "Project rootPath is required"),
  description: z.string().trim().optional().transform((value) => value || undefined),
  includeGlobs: z.array(z.string().trim().min(1)).optional(),
  excludeGlobs: z.array(z.string().trim().min(1)).optional(),
  excludePaths: z.array(z.string().trim().min(1)).optional(),
  excludeExtensions: z.array(z.string().trim().min(1)).optional(),
  maxFileSizeBytes: z.number().int().positive().optional(),
});

export const searchRequestSchema = z.object({
  query: z.string().trim().min(3, "Query must contain at least 3 characters"),
  limit: z.number().int().finite().optional().default(6).transform((value) => {
    return Math.min(Math.max(value, 1), 20);
  }),
});

export const contextRequestSchema = z.object({
  query: z.string().trim().min(3, "Query must contain at least 3 characters"),
});

export function parseBrowserApiBody<T>(
  schema: z.ZodSchema<T>,
  payload: unknown,
): T {
  const result = schema.safeParse(payload);
  if (!result.success) {
    const issue = result.error.issues[0];
    throw new HttpError(400, issue?.message ?? "Request body is invalid");
  }

  return result.data;
}
