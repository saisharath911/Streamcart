export type OrderStatus = "AwaitingInventory" | "AwaitingPayment" | "Confirmed" | "Compensating" | "Cancelled";
export type TimelineKind = "Progress" | "Success" | "Compensation" | "Refund" | "Ignored";

export interface OrderSummary {
  id: string;
  customerId: string;
  status: OrderStatus;
  total: number | null;
  cancellationReason: string | null;
  createdAt: string;
  statusChangedAt: string;
  lineCount: number;
}

export interface TimelineEntry {
  at: string;
  trigger: string;
  narrative: string;
  kind: TimelineKind;
  statusAfter: OrderStatus;
  sent: string[];
}

export interface OrderDetails {
  summary: OrderSummary;
  lines: { sku: string; quantity: number; unitPrice: number | null }[];
  timeline: TimelineEntry[];
}

export interface OrderStats {
  byStatus: Record<OrderStatus, number>;
  total: number;
  averageConfirmSeconds: number | null;
  p95ConfirmSeconds: number | null;
}

export interface Product {
  sku: string;
  name: string;
  category: string;
  unitPrice: number;
  available: number;
  reserved: number;
}

export interface ChaosConfig {
  failureRate: number;
  latencyMs: number;
  duplicateDeliveryRate: number;
}

export interface ChaosResponse {
  current: ChaosConfig;
  presets: string[];
}

export interface MessagingStats {
  service: string;
  handled: number;
  duplicatesSuppressed: number;
  handlerFailures: number;
  published: number;
  publishFailures: number;
  acksSkippedByChaos: number;
}

export interface PlaceOrderBody {
  customerId: string;
  items: { sku: string; quantity: number }[];
}

export interface PlaceOrderResult {
  order: OrderDetails | null;
  replayed: boolean;
  status: number;
  error?: string;
}

async function json<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: { "Content-Type": "application/json", ...init?.headers },
  });
  if (!response.ok) {
    throw new Error(`${init?.method ?? "GET"} ${path} returned ${response.status}`);
  }
  return (await response.json()) as T;
}

export const api = {
  orders: (take = 40) => json<OrderSummary[]>(`/api/orders?take=${take}`),
  order: (id: string) => json<OrderDetails>(`/api/orders/${id}`),
  stats: () => json<OrderStats>("/api/orders/stats"),
  products: () => json<Product[]>("/api/products"),
  restock: (sku: string, quantity: number) =>
    json<Product>(`/api/products/${encodeURIComponent(sku)}/restock`, {
      method: "POST",
      body: JSON.stringify({ quantity }),
    }),
  chaos: () => json<ChaosResponse>("/api/chaos"),
  setChaos: (config: ChaosConfig) => json<ChaosResponse>("/api/chaos", { method: "PUT", body: JSON.stringify(config) }),
  applyPreset: (name: string) =>
    json<ChaosResponse>(`/api/chaos/presets/${encodeURIComponent(name)}`, { method: "POST" }),
  messagingStats: (service: "orders" | "inventory" | "payments") =>
    json<MessagingStats>(`/api/${service}/messaging/stats`),

  async placeOrder(body: PlaceOrderBody, idempotencyKey: string): Promise<PlaceOrderResult> {
    const response = await fetch("/api/orders", {
      method: "POST",
      headers: { "Content-Type": "application/json", "Idempotency-Key": idempotencyKey },
      body: JSON.stringify(body),
    });
    if (!response.ok) {
      const problem = (await response.json().catch(() => null)) as { detail?: string; title?: string } | null;
      return {
        order: null,
        replayed: false,
        status: response.status,
        error: problem?.detail ?? problem?.title ?? `Request failed with ${response.status}`,
      };
    }
    return {
      order: (await response.json()) as OrderDetails,
      replayed: response.headers.get("Idempotent-Replay") === "true",
      status: response.status,
    };
  },
};
