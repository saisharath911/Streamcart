import type { OrderSummary } from "../api";
import { OrderRoute, STATUS_LABEL } from "./OrderRoute";
import { money, shortId, timeOfDay } from "../format";

interface Props {
  orders: OrderSummary[];
  selectedId: string | null;
  onSelect: (id: string) => void;
}

export function OrderList({ orders, selectedId, onSelect }: Props) {
  if (orders.length === 0) {
    return (
      <div className="empty">
        <p className="empty-title">No orders yet</p>
        <p>Place one above, or send a burst of 20 to see the sagas run side by side.</p>
      </div>
    );
  }

  return (
    <ol className="orders" aria-label="Recent orders, newest first">
      {orders.map((order) => (
        <li key={order.id}>
          <button
            type="button"
            className={`order status--${order.status}`}
            aria-pressed={order.id === selectedId}
            onClick={() => onSelect(order.id)}
          >
            <span className="order-head">
              <span className="order-id">#{shortId(order.id)}</span>
              <span className="order-status">{STATUS_LABEL[order.status]}</span>
            </span>
            <OrderRoute order={order} />
            <span className="order-meta">
              <span>{order.customerId}</span>
              <span className="num">{order.total === null ? "Not priced" : money(order.total)}</span>
              <span className="num">{timeOfDay(order.createdAt)}</span>
            </span>
          </button>
        </li>
      ))}
    </ol>
  );
}
