import { RevitClientConnection } from "./SocketClient.js";
import { currentRevitTarget } from "./revitTarget.js";

// One mutex per Revit host:port - serializes requests to the same Revit
// instance (prevents race conditions) without blocking other users' Revit
const connectionMutexes = new Map<string, Promise<void>>();

/**
 * Connect to the Revit client and run an operation
 * @param operation Operation to run once connected
 * @returns The result of the operation
 */
export async function withRevitConnection<T>(
  operation: (client: RevitClientConnection) => Promise<T>
): Promise<T> {
  const target = currentRevitTarget();
  const key = `${target.host}:${target.port}`;

  // Wait for any pending connection to the same Revit to complete before starting a new one
  const previousMutex = connectionMutexes.get(key) ?? Promise.resolve();
  let releaseMutex: () => void;
  const mutex = new Promise<void>((resolve) => {
    releaseMutex = resolve;
  });
  connectionMutexes.set(key, mutex);
  await previousMutex;

  const revitClient = new RevitClientConnection(target.host, target.port);

  try {
    // Connect to the Revit client
    if (!revitClient.isConnected) {
      await new Promise<void>((resolve, reject) => {
        const onConnect = () => {
          revitClient.socket.removeListener("connect", onConnect);
          revitClient.socket.removeListener("error", onError);
          resolve();
        };

        const onError = (error: any) => {
          revitClient.socket.removeListener("connect", onConnect);
          revitClient.socket.removeListener("error", onError);
          reject(new Error("connect to revit client failed"));
        };

        revitClient.socket.on("connect", onConnect);
        revitClient.socket.on("error", onError);

        revitClient.connect();

        setTimeout(() => {
          revitClient.socket.removeListener("connect", onConnect);
          revitClient.socket.removeListener("error", onError);
          reject(new Error("Failed to connect to the Revit client"));
        }, 5000);
      });
    }

    // Run the operation
    return await operation(revitClient);
  } finally {
    // Disconnect
    revitClient.disconnect();
    // Release the mutex so the next request can proceed
    releaseMutex!();
    if (connectionMutexes.get(key) === mutex) {
      connectionMutexes.delete(key);
    }
  }
}
