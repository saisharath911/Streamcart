const currency = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD" });
const clock = new Intl.DateTimeFormat("en-US", { hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });

export const money = (value: number) => currency.format(value);
export const shortId = (id: string) => id.slice(-6).toUpperCase();
export const timeOfDay = (iso: string) => clock.format(new Date(iso));

export function elapsed(fromIso: string, toIso: string) {
  const ms = new Date(toIso).getTime() - new Date(fromIso).getTime();
  return ms < 1000 ? `+${ms} ms` : `+${(ms / 1000).toFixed(1)} s`;
}

/** "InventoryReserved" -> "Inventory reserved" */
export function humanize(type: string) {
  const words = type.replace(/([a-z])([A-Z])/g, "$1 $2").toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}
