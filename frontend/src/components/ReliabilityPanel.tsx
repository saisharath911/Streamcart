import { api, type MessagingStats, type OrderStats, type Product } from "../api";
import { money } from "../format";

interface Props {
  stats: OrderStats | null;
  messaging: MessagingStats[];
  products: Product[];
  onRestocked: () => void;
}

const fmtSeconds = (s: number | null) => (s === null ? "–" : s < 1 ? `${Math.round(s * 1000)} ms` : `${s.toFixed(1)} s`);

export function ReliabilityPanel({ stats, messaging, products, onRestocked }: Props) {
  const inFlight = stats
    ? stats.byStatus.AwaitingInventory + stats.byStatus.AwaitingPayment + stats.byStatus.Compensating
    : 0;
  const duplicates = messaging.reduce((sum, m) => sum + m.duplicatesSuppressed, 0);
  const maxStock = Math.max(1, ...products.map((p) => p.available + p.reserved));

  return (
    <div className="reliability">
      <section aria-labelledby="outcomes-title">
        <h2 id="outcomes-title" className="section-title">
          Outcomes
        </h2>
        <dl className="figures">
          <div>
            <dt>Confirmed</dt>
            <dd className="num tone-ok">{stats?.byStatus.Confirmed ?? "–"}</dd>
          </div>
          <div>
            <dt>Cancelled and undone</dt>
            <dd className="num tone-back">{stats?.byStatus.Cancelled ?? "–"}</dd>
          </div>
          <div>
            <dt>In flight</dt>
            <dd className="num">{stats ? inFlight : "–"}</dd>
          </div>
          <div>
            <dt>Time to confirm, p95</dt>
            <dd className="num">{fmtSeconds(stats?.p95ConfirmSeconds ?? null)}</dd>
          </div>
          <div>
            <dt>Duplicate deliveries discarded</dt>
            <dd className="num">{duplicates}</dd>
          </div>
        </dl>
      </section>

      <section aria-labelledby="services-title">
        <h2 id="services-title" className="section-title">
          Message handling by service
        </h2>
        <table className="services">
          <thead>
            <tr>
              <th scope="col">Service</th>
              <th scope="col" className="num">Handled</th>
              <th scope="col" className="num">Duplicates</th>
              <th scope="col" className="num">Retries</th>
              <th scope="col" className="num">Published</th>
            </tr>
          </thead>
          <tbody>
            {messaging.map((m) => (
              <tr key={m.service}>
                <th scope="row">{m.service.charAt(0).toUpperCase() + m.service.slice(1)}</th>
                <td className="num">{m.handled}</td>
                <td className="num">{m.duplicatesSuppressed}</td>
                <td className="num">{m.handlerFailures}</td>
                <td className="num">{m.published}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      <section aria-labelledby="stock-title">
        <h2 id="stock-title" className="section-title">
          Stock
        </h2>
        <ul className="stock">
          {products.map((p) => (
            <li key={p.sku}>
              <span className="stock-name">
                {p.name}
                <span className="stock-price num">{money(p.unitPrice)}</span>
              </span>
              <span className="stock-bar" aria-hidden="true">
                <span className="stock-available" style={{ width: `${(p.available / maxStock) * 100}%` }} />
                <span className="stock-reserved" style={{ width: `${(p.reserved / maxStock) * 100}%` }} />
              </span>
              <span className="stock-count num">
                {p.available} free, {p.reserved} held
              </span>
              <button
                type="button"
                className="btn btn--small"
                onClick={() => void api.restock(p.sku, 20).then(onRestocked)}
                aria-label={`Add 20 ${p.name}`}
              >
                +20
              </button>
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}
