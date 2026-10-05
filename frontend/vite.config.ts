import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Mirrors the nginx / ALB path routing so the dashboard always talks to one origin.
const orders = "http://localhost:5101";
const inventory = "http://localhost:5102";
const payments = "http://localhost:5103";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api/orders": orders,
      "/hubs": { target: orders, ws: true },
      "/api/products": inventory,
      "/api/reservations": inventory,
      "/api/inventory": inventory,
      "/api/payments": payments,
      "/api/chaos": payments,
    },
  },
});
