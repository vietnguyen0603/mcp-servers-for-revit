import { z } from "zod";
import { withRevitConnection } from "./ConnectionManager.js";
import { applyBatchOutcome } from "./batchOutcome.js";

/** Shared input schemas and transport for the view, sheet and annotation tools. */

export const elementIdSchema = z.number().int().positive();

export const point2Schema = z.object({
  x: z.number().finite().describe("X in millimetres"),
  y: z.number().finite().describe("Y in millimetres"),
});

export const point3Schema = point2Schema.extend({
  z: z.number().finite().optional().describe("Z in millimetres (default 0)"),
});

export const parameterValuesSchema = z
  .record(z.string().min(1).max(256), z.union([z.string().max(4096), z.number().finite(), z.boolean()]))
  .refine((values) => Object.keys(values).length <= 64, { message: "At most 64 parameter values" });

interface RevitResult {
  success?: boolean;
  /** Command-set handlers serialise AIResult with PascalCase names. */
  Success?: boolean;
  message?: string;
}

/**
 * Sends a command to Revit and renders the `{ success, message, response }`
 * result as MCP text content, flagging `success: false` as an error. Batch
 * results whose items all failed are errors too; partial failures get a
 * WARNING line (see batchOutcome.ts).
 */
export async function sendDocumentationCommand(command: string, params: unknown) {
  try {
    const response = await withRevitConnection(async (revitClient) => {
      return await revitClient.sendCommand(command, params);
    });
    const result = response as RevitResult | null;
    const failed = result?.success === false || result?.Success === false;
    return applyBatchOutcome(response, {
      content: [{ type: "text" as const, text: JSON.stringify(response, null, 2) }],
      ...(failed ? { isError: true } : {}),
    });
  } catch (error) {
    return {
      content: [
        {
          type: "text" as const,
          text: `${command} failed: ${error instanceof Error ? error.message : String(error)}`,
        },
      ],
      isError: true,
    };
  }
}
