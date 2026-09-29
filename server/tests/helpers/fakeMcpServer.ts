import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z, type ZodRawShape } from "zod";

export interface McpTextResponse {
  content: Array<{ type: string; text: string }>;
  isError?: boolean;
}

export interface RegisteredTool {
  name: string;
  description: string;
  shape: ZodRawShape;
  /** Parse raw caller input through the tool's Zod shape (applies defaults). */
  parse: (args?: Record<string, unknown>) => Record<string, unknown>;
  /** Parse input, then invoke the registered handler with a fake extra context. */
  invoke: (args?: Record<string, unknown>) => Promise<McpTextResponse>;
}

export interface FakeMcpServer {
  server: McpServer;
  tools: Map<string, RegisteredTool>;
  getTool: (name: string) => RegisteredTool;
}

/**
 * Minimal stand-in for the MCP SDK `McpServer` that captures the tool
 * registration call and reproduces the SDK behaviour the facades rely on:
 * the input is validated against the Zod shape before the handler runs, so
 * field defaults are applied.
 */
export function createFakeMcpServer(): FakeMcpServer {
  const tools = new Map<string, RegisteredTool>();

  const server = {
    tool(
      name: string,
      description: string,
      shape: ZodRawShape,
      handler: (args: any, extra: any) => Promise<McpTextResponse>
    ) {
      const schema = z.object(shape);
      const registered: RegisteredTool = {
        name,
        description,
        shape,
        parse: (args = {}) => schema.parse(args) as Record<string, unknown>,
        invoke: async (args = {}) => handler(schema.parse(args), {}),
      };
      tools.set(name, registered);
      return undefined;
    },
  } as unknown as McpServer;

  const getTool = (name: string): RegisteredTool => {
    const tool = tools.get(name);
    if (!tool) {
      throw new Error(`Tool "${name}" was not registered`);
    }
    return tool;
  };

  return { server, tools, getTool };
}
