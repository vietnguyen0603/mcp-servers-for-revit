import { vi } from "vitest";

/**
 * Mock boundary for `src/utils/ConnectionManager.ts`. Tool tests mock the real
 * module with this one so handlers can be exercised without a Revit listener.
 *
 * `sendCommand` records the dispatched JSON-RPC method and params;
 * `withRevitConnection` hands the fake client to the operation and propagates
 * errors exactly like the real connection wrapper.
 */
export const sendCommand = vi.fn();

export type SendCommandMock = typeof sendCommand;

export const withRevitConnection = vi.fn(
  (operation: (client: { sendCommand: SendCommandMock }) => unknown) =>
    operation({ sendCommand })
);

/** Clear recorded calls and restore the default pass-through behaviour. */
export function resetConnectionMock(): void {
  sendCommand.mockReset();
  withRevitConnection.mockReset();
  withRevitConnection.mockImplementation(
    (operation: (client: { sendCommand: SendCommandMock }) => unknown) =>
      operation({ sendCommand })
  );
}
