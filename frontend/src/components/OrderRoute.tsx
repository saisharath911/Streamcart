import type { OrderSummary } from "../api";

/**
 * Draws an order's saga as a transit line: Placed → Stock → Payment → Confirmed.
 * Forward progress runs left to right; compensation is drawn as the line doubling back
 * underneath, so a reversed journey is visible at a glance.
 */
const X = [10, 88, 166, 244];
const Y = 14;
const STATIONS = ["Placed", "Stock", "Payment", "Confirmed"];

type StationState = "done" | "next" | "todo" | "failed" | "confirmed";

function layout(order: OrderSummary) {
  const s: StationState[] = ["done", "todo", "todo", "todo"];
  let flowing = -1; // index of the segment currently in flight (from station i to i+1)
  let reverse: "none" | "running" | "done" = "none";

  switch (order.status) {
    case "AwaitingInventory":
      s[1] = "next";
      flowing = 0;
      break;
    case "AwaitingPayment":
      s[1] = "done";
      s[2] = "next";
      flowing = 1;
      break;
    case "Confirmed":
      s[1] = s[2] = "done";
      s[3] = "confirmed";
      break;
    case "Compensating":
      s[1] = "done";
      s[2] = "failed";
      reverse = "running";
      break;
    case "Cancelled":
      if (order.total === null) {
        s[1] = "failed"; // rejected at stock: nothing to undo
      } else {
        s[1] = "done";
        s[2] = "failed";
        reverse = "done";
      }
      break;
  }
  return { s, flowing, reverse };
}

export function OrderRoute({ order }: { order: OrderSummary }) {
  const { s, flowing, reverse } = layout(order);
  const lastReached = s.reduce((acc, state, i) => (state === "done" || state === "confirmed" || state === "failed" ? i : acc), 0);

  return (
    <svg className="route" viewBox="0 0 254 40" role="img" aria-label={`Order route, ${order.status}`}>
      <line className="route-track" x1={X[0]} y1={Y} x2={X[3]} y2={Y} />
      <line
        className={order.status === "Confirmed" ? "route-run route-run--ok" : "route-run"}
        x1={X[0]}
        y1={Y}
        x2={X[lastReached]}
        y2={Y}
      />
      {flowing >= 0 && <line className="route-flow" x1={X[flowing]} y1={Y} x2={X[flowing + 1]} y2={Y} />}
      {reverse !== "none" && (
        <path
          className={reverse === "running" ? "route-back route-back--running" : "route-back"}
          d={`M ${X[2]} ${Y} C ${X[2]} ${Y + 20}, ${X[1]} ${Y + 20}, ${X[1]} ${Y + 4}`}
        />
      )}
      {X.map((x, i) => (
        <circle key={STATIONS[i]} className={`station station--${s[i]}`} cx={x} cy={Y} r={s[i] === "confirmed" ? 6 : 5}>
          <title>{STATIONS[i]}</title>
        </circle>
      ))}
    </svg>
  );
}

export const STATUS_LABEL: Record<OrderSummary["status"], string> = {
  AwaitingInventory: "Reserving stock",
  AwaitingPayment: "Taking payment",
  Confirmed: "Confirmed",
  Compensating: "Undoing",
  Cancelled: "Cancelled",
};
