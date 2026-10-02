import { z } from "zod";

/** Absolute Windows path: drive-letter (C:\ or C:/) or UNC (\\server\share). */
export const absoluteFolderSchema = z
  .string()
  .min(3)
  .max(240)
  .regex(/^([A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+)/, "folder must be an absolute Windows path, e.g. C:\\Exports")
  .refine((folder) => !/[<>"|?*\u0000-\u001f]/.test(folder.slice(2)), {
    message: "folder contains invalid path characters",
  });
