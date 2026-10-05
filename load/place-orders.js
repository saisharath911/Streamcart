// k6 load test: sustained order placement plus an idempotent-retry check.
//   k6 run load/place-orders.js                         (local, via the dashboard proxy)
//   BASE_URL=https://dxxxx.cloudfront.net k6 run load/place-orders.js
import http from "k6/http";
import { check } from "k6";
import { Rate, Trend } from "k6/metrics";

const BASE_URL = __ENV.BASE_URL || "http://localhost:8080";
const SKUS = ["KB-75", "MS-ERGO", "MON-27Q", "DOCK-TB4", "HP-ANC"];

const replayCorrect = new Rate("idempotent_replay_correct");
const placeLatency = new Trend("place_order_latency", true);

export const options = {
  scenarios: {
    steady: {
      executor: "ramping-arrival-rate",
      startRate: 5,
      timeUnit: "1s",
      preAllocatedVUs: 20,
      maxVUs: 100,
      stages: [
        { target: 25, duration: "30s" },
        { target: 50, duration: "1m" },
        { target: 0, duration: "15s" },
      ],
    },
  },
  thresholds: {
    http_req_failed: ["rate<0.01"],
    place_order_latency: ["p(95)<300"],
    idempotent_replay_correct: ["rate==1"],
  },
};

function uuid() {
  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    return (c === "x" ? r : (r & 0x3) | 0x8).toString(16);
  });
}

export default function () {
  const key = uuid();
  const body = JSON.stringify({
    customerId: `load-${__VU}`,
    items: [{ sku: SKUS[Math.floor(Math.random() * SKUS.length)], quantity: 1 }],
  });
  const params = { headers: { "Content-Type": "application/json", "Idempotency-Key": key } };

  const first = http.post(`${BASE_URL}/api/orders`, body, params);
  placeLatency.add(first.timings.duration);
  check(first, { "order created": (r) => r.status === 201 });

  // 10% of iterations retry with the same key, as a client would after a timeout.
  if (Math.random() < 0.1 && first.status === 201) {
    const retry = http.post(`${BASE_URL}/api/orders`, body, params);
    replayCorrect.add(
      retry.status === 200 &&
        retry.headers["Idempotent-Replay"] === "true" &&
        retry.json("summary.id") === first.json("summary.id"),
    );
  }
}
