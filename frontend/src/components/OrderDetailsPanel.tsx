import type { OrderDetails } from "../api";
import { elapsed, humanize, money, shortId } from "../format";
import { OrderRoute, STATUS_LABEL } from "./OrderRoute";

export function OrderDetailsPanel({ details }: { details: OrderDetails | null }) {
  if (!details) {
    return (
      <div className="empty">
        <p className="empty-title">Pick an order</p>
        <p>Its timeline shows every message the saga received, what it decided, and what it sent next.</p>
      </div>
    );
  }

  const { summary, lines, timeline } = details;
  const start = timeline[0]?.at ?? summary.createdAt;

  return (
    <article className="details">
      <header className="details-head">
        <h2>Order #{shortId(summary.id)}</h2>
        <p className={`pill status--${summary.status}`}>{STATUS_LABEL[summary.status]}</p>
      </header>

      <OrderRoute order={summary} />

      {summary.cancellationReason && <p className="reason">{summary.cancellationReason}</p>}

      <table className="lines">
        <caption className="visually-hidden">Order lines</caption>
        <thead>
          <tr>
            <th scope="col">Item</th>
            <th scope="col" className="num">Qty</th>
            <th scope="col" className="num">Price</th>
          </tr>
        </thead>
        <tbody>
          {lines.map((l) => (
            <tr key={l.sku}>
              <td>{l.sku}</td>
              <td className="num">{l.quantity}</td>
              <td className="num">{l.unitPrice === null ? "–" : money(l.unitPrice)}</td>
            </tr>
          ))}
        </tbody>
        {summary.total !== null && (
          <tfoot>
            <tr>
              <td colSpan={2}>Total</td>
              <td className="num">{money(summary.total)}</td>
            </tr>
          </tfoot>
        )}
      </table>

      <h3 className="section-title">Saga timeline</h3>
      <ol className="timeline">
        {timeline.map((entry, i) => (
          <li key={`${entry.at}-${i}`} className={`event kind--${entry.kind}`}>
            <div className="event-when num">{i === 0 ? "start" : elapsed(start, entry.at)}</div>
            <div className="event-body">
              <p className="event-trigger">{humanize(entry.trigger)}</p>
              <p className="event-narrative">{entry.narrative}</p>
              {entry.sent.length > 0 && (
                <p className="event-sent">
                  Sent {entry.sent.map((s) => <span key={s} className="chip">{humanize(s)}</span>)}
                </p>
              )}
            </div>
          </li>
        ))}
      </ol>
    </article>
  );
}
