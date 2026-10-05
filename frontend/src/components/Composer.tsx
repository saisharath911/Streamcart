import { useState } from "react";
import { api, type PlaceOrderBody, type Product } from "../api";
import { shortId } from "../format";

const CUSTOMERS = ["ada", "grace", "linus", "margaret", "barbara", "ken", "dennis", "radia"];

const pick = <T,>(items: T[]) => items[Math.floor(Math.random() * items.length)];

function randomOrder(products: Product[]): PlaceOrderBody {
  const count = 1 + Math.floor(Math.random() * 2);
  const chosen = [...products].sort(() => Math.random() - 0.5).slice(0, count);
  return {
    customerId: pick(CUSTOMERS),
    items: chosen.map((p) => ({ sku: p.sku, quantity: 1 + Math.floor(Math.random() * 2) })),
  };
}

interface Props {
  products: Product[];
  onPlaced: (orderId: string) => void;
}

export function Composer({ products, onPlaced }: Props) {
  const [sku, setSku] = useState("");
  const [quantity, setQuantity] = useState(1);
  const [last, setLast] = useState<{ body: PlaceOrderBody; key: string } | null>(null);
  const [feedback, setFeedback] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const selectedSku = sku || products[0]?.sku || "";

  const send = async (body: PlaceOrderBody, key: string) => {
    const result = await api.placeOrder(body, key);
    if (!result.order) {
      setFeedback(result.error ?? "The order was not accepted.");
      return null;
    }
    return result;
  };

  const placeOne = async () => {
    if (!selectedSku) return;
    setBusy(true);
    const body = { customerId: pick(CUSTOMERS), items: [{ sku: selectedSku, quantity }] };
    const key = crypto.randomUUID();
    const result = await send(body, key);
    if (result?.order) {
      setLast({ body, key });
      setFeedback(`Placed order #${shortId(result.order.summary.id)}.`);
      onPlaced(result.order.summary.id);
    }
    setBusy(false);
  };

  const resend = async () => {
    if (!last) return;
    setBusy(true);
    const result = await send(last.body, last.key);
    if (result?.order) {
      setFeedback(
        result.replayed
          ? `Same key, same answer: the API returned order #${shortId(result.order.summary.id)} again instead of creating a second one.`
          : `Created order #${shortId(result.order.summary.id)}.`,
      );
    }
    setBusy(false);
  };

  const burst = async () => {
    if (products.length === 0) return;
    setBusy(true);
    const results = await Promise.all(
      Array.from({ length: 20 }, () => send(randomOrder(products), crypto.randomUUID())),
    );
    setFeedback(`Sent ${results.filter(Boolean).length} orders at once.`);
    setBusy(false);
  };

  return (
    <section className="composer" aria-labelledby="composer-title">
      <h2 id="composer-title" className="section-title">
        Place orders
      </h2>
      <div className="composer-row">
        <label className="field">
          <span>Item</span>
          <select value={selectedSku} onChange={(e) => setSku(e.target.value)} disabled={products.length === 0}>
            {products.map((p) => (
              <option key={p.sku} value={p.sku}>
                {p.name} ({p.available} left)
              </option>
            ))}
          </select>
        </label>
        <label className="field field--narrow">
          <span>Qty</span>
          <input
            type="number"
            min={1}
            max={10}
            value={quantity}
            onChange={(e) => setQuantity(Math.min(10, Math.max(1, Number(e.target.value) || 1)))}
          />
        </label>
        <button type="button" className="btn btn--primary" onClick={() => void placeOne()} disabled={busy || !selectedSku}>
          Place order
        </button>
        <button
          type="button"
          className="btn"
          onClick={() => void resend()}
          disabled={busy || !last}
          title="Sends the previous request again with the same Idempotency-Key"
        >
          Resend last request
        </button>
        <button type="button" className="btn" onClick={() => void burst()} disabled={busy || products.length === 0}>
          Send 20 random orders
        </button>
      </div>
      <p className="feedback" aria-live="polite">
        {feedback ?? (products.length === 0 ? "Waiting for the catalog from the inventory service." : " ")}
      </p>
    </section>
  );
}
