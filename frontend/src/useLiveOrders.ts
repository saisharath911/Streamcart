import { useEffect, useRef, useState } from "react";
import { HubConnectionBuilder, HubConnectionState, HttpTransportType, LogLevel } from "@microsoft/signalr";
import { api, type OrderSummary, type TimelineEntry } from "./api";

export type LinkState = "connecting" | "live" | "reconnecting" | "offline";

interface OrderUpdated {
  order: OrderSummary;
  entry: TimelineEntry | null;
}

const MAX_ORDERS = 60;

/**
 * Loads recent orders once, then keeps them current from the SignalR "orderUpdated" stream.
 * WebSockets-only with skipNegotiation, so no sticky sessions are needed behind a load balancer.
 */
export function useLiveOrders(onEntry: (orderId: string, entry: TimelineEntry) => void) {
  const [orders, setOrders] = useState<OrderSummary[]>([]);
  const [link, setLink] = useState<LinkState>("connecting");
  const onEntryRef = useRef(onEntry);
  onEntryRef.current = onEntry;

  useEffect(() => {
    let disposed = false;

    api.orders(MAX_ORDERS).then((initial) => !disposed && setOrders(initial)).catch(() => setLink("offline"));

    const connection = new HubConnectionBuilder()
      .withUrl("/hubs/orders", { transport: HttpTransportType.WebSockets, skipNegotiation: true })
      .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on("orderUpdated", ({ order, entry }: OrderUpdated) => {
      setOrders((current) => {
        const rest = current.filter((o) => o.id !== order.id);
        return [order, ...rest]
          .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
          .slice(0, MAX_ORDERS);
      });
      if (entry) onEntryRef.current(order.id, entry);
    });

    connection.onreconnecting(() => setLink("reconnecting"));
    connection.onreconnected(() => {
      setLink("live");
      // Catch up on anything missed while disconnected.
      api.orders(MAX_ORDERS).then(setOrders).catch(() => undefined);
    });
    connection.onclose(() => setLink("offline"));

    connection
      .start()
      .then(() => !disposed && setLink("live"))
      .catch(() => !disposed && setLink("offline"));

    return () => {
      disposed = true;
      if (connection.state !== HubConnectionState.Disconnected) void connection.stop();
    };
  }, []);

  return { orders, link };
}

/** Polls a loader on an interval; returns the latest value (or null until the first success). */
export function usePolling<T>(load: () => Promise<T>, intervalMs: number, deps: unknown[] = []) {
  const [value, setValue] = useState<T | null>(null);
  const loadRef = useRef(load);
  loadRef.current = load;

  useEffect(() => {
    let active = true;
    const tick = () =>
      loadRef
        .current()
        .then((v) => active && setValue(v))
        .catch(() => undefined);
    void tick();
    const handle = window.setInterval(tick, intervalMs);
    return () => {
      active = false;
      window.clearInterval(handle);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [intervalMs, ...deps]);

  return value;
}
