import { useCallback, useEffect, useState } from "react";
import { api, type MessagingStats, type OrderDetails, type TimelineEntry } from "./api";
import { useLiveOrders, usePolling } from "./useLiveOrders";
import { OrderList } from "./components/OrderList";
import { OrderDetailsPanel } from "./components/OrderDetailsPanel";
import { ChaosPanel } from "./components/ChaosPanel";
import { Composer } from "./components/Composer";
import { ReliabilityPanel } from "./components/ReliabilityPanel";

const LINK_TEXT = {
  connecting: "Connecting to live updates",
  live: "Live",
  reconnecting: "Reconnecting",
  offline: "Live updates offline",
} as const;

const SERVICES = ["orders", "inventory", "payments"] as const;

export default function App() {
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [details, setDetails] = useState<OrderDetails | null>(null);
  const [stockVersion, setStockVersion] = useState(0);

  const onEntry = useCallback(
    (orderId: string, entry: TimelineEntry) => {
      if (orderId !== selectedId) return;
      // Append the pushed entry instead of refetching the whole order.
      setDetails((d) => (d && d.summary.id === orderId ? { ...d, timeline: [...d.timeline, entry] } : d));
    },
    [selectedId],
  );

  const { orders, link } = useLiveOrders(onEntry);

  useEffect(() => {
    if (!selectedId) return;
    let active = true;
    api.order(selectedId).then((d) => active && setDetails(d)).catch(() => active && setDetails(null));
    return () => {
      active = false;
    };
  }, [selectedId]);

  // Keep the selected summary (status, total) in step with the live list.
  const liveSummary = orders.find((o) => o.id === selectedId);
  const shownDetails = details && liveSummary ? { ...details, summary: liveSummary } : details;

  const stats = usePolling(api.stats, 3000);
  const products = usePolling(api.products, 3000, [stockVersion]) ?? [];
  const messaging =
    usePolling(
      () => Promise.all(SERVICES.map((s) => api.messagingStats(s).catch(() => null))),
      3000,
    )?.filter((m): m is MessagingStats => m !== null) ?? [];

  return (
    <div className="app">
      <header className="masthead">
        <div>
          <h1>StreamCart Saga Lab</h1>
          <p className="lede">
            Orders run across three services over SNS and SQS. Break things on purpose and watch every order end
            confirmed or fully undone.
          </p>
        </div>
        <p className={`link link--${link}`} role="status">
          {LINK_TEXT[link]}
        </p>
      </header>

      <div className="controls">
        <Composer products={products} onPlaced={setSelectedId} />
        <ChaosPanel />
      </div>

      <main className="workspace">
        <section className="list-pane" aria-labelledby="orders-title">
          <h2 id="orders-title" className="section-title">
            Orders
          </h2>
          <OrderList orders={orders} selectedId={selectedId} onSelect={setSelectedId} />
        </section>
        <section className="details-pane" aria-label="Selected order">
          <OrderDetailsPanel details={shownDetails} />
        </section>
      </main>

      <ReliabilityPanel
        stats={stats}
        messaging={messaging}
        products={products}
        onRestocked={() => setStockVersion((v) => v + 1)}
      />

      <footer className="footer">
        <span>Built with .NET 10, EF Core, PostgreSQL, Amazon SNS and SQS, SignalR and React.</span>
        <a href="http://localhost:16686" target="_blank" rel="noreferrer">
          Open traces in Jaeger
        </a>
      </footer>
    </div>
  );
}
