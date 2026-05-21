import { createGrpcWebTransport } from "@connectrpc/connect-web";
import { createClient } from "@connectrpc/connect";
import { PingService } from "@gen/carpe_momentum/v1/ping_pb";

// Default adapter endpoint. Override in Settings (Phase 5) so the
// adapter can be moved to a dedicated machine — see SPEC §2.
const baseUrl =
  (import.meta as ImportMeta & { env: { VITE_ADAPTER_URL?: string } }).env
    ?.VITE_ADAPTER_URL ?? "http://localhost:5000";

export const transport = createGrpcWebTransport({
  baseUrl,
});

export const pingClient = createClient(PingService, transport);
