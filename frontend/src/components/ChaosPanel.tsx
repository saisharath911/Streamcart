import { useEffect, useState } from "react";
import { api, type ChaosConfig } from "../api";

const SCENARIOS: { id: string; name: string; explains: string }[] = [
  { id: "calm", name: "Calm", explains: "No injected faults. Every order should confirm in well under a second." },
  {
    id: "flaky-payments",
    name: "Flaky payments",
    explains: "35% of cards are declined. Watch those orders release their stock and cancel.",
  },
  {
    id: "slow-gateway",
    name: "Slow gateway",
    explains:
      "Payments take 20 s, longer than the saga's 15 s step timeout. Orders compensate, then the late payment is refunded automatically.",
  },
  {
    id: "duplicate-storm",
    name: "Duplicate storm",
    explains:
      "Half of processed payment messages are never acknowledged, so SQS delivers them again. The inbox discards every duplicate: no double charges.",
  },
  {
    id: "everything-on-fire",
    name: "Everything on fire",
    explains: "Declines, latency and duplicate deliveries at once. Every order still ends confirmed or fully undone.",
  },
];

function matchPreset(config: ChaosConfig): string | null {
  const presets: Record<string, ChaosConfig> = {
    calm: { failureRate: 0, latencyMs: 0, duplicateDeliveryRate: 0 },
    "flaky-payments": { failureRate: 0.35, latencyMs: 300, duplicateDeliveryRate: 0 },
    "slow-gateway": { failureRate: 0, latencyMs: 20000, duplicateDeliveryRate: 0 },
    "duplicate-storm": { failureRate: 0, latencyMs: 0, duplicateDeliveryRate: 0.5 },
    "everything-on-fire": { failureRate: 0.25, latencyMs: 4000, duplicateDeliveryRate: 0.4 },
  };
  const hit = Object.entries(presets).find(
    ([, p]) =>
      p.failureRate === config.failureRate &&
      p.latencyMs === config.latencyMs &&
      p.duplicateDeliveryRate === config.duplicateDeliveryRate,
  );
  return hit?.[0] ?? null;
}

export function ChaosPanel() {
  const [config, setConfig] = useState<ChaosConfig | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api
      .chaos()
      .then((r) => setConfig(r.current))
      .catch(() => setError("Payments service is not reachable, so fault injection is unavailable."));
  }, []);

  const active = config ? matchPreset(config) : null;
  const activeScenario = SCENARIOS.find((s) => s.id === active);

  const apply = async (id: string) => {
    try {
      setConfig((await api.applyPreset(id)).current);
      setError(null);
    } catch {
      setError("Could not switch scenario. Check that the payments service is running.");
    }
  };

  const tune = async (patch: Partial<ChaosConfig>) => {
    if (!config) return;
    try {
      setConfig((await api.setChaos({ ...config, ...patch })).current);
    } catch {
      setError("Could not save the custom settings.");
    }
  };

  return (
    <section className="chaos" aria-labelledby="chaos-title">
      <h2 id="chaos-title" className="section-title">
        Fault injection
      </h2>
      <div className="scenarios" role="radiogroup" aria-label="Fault scenario">
        {SCENARIOS.map((s) => (
          <button
            key={s.id}
            type="button"
            role="radio"
            aria-checked={active === s.id}
            className="scenario"
            disabled={!config}
            onClick={() => void apply(s.id)}
          >
            {s.name}
          </button>
        ))}
      </div>
      <p className="scenario-explains" aria-live="polite">
        {error ?? activeScenario?.explains ?? "Custom settings."}
      </p>

      {config && (
        <details className="tuning">
          <summary>Fine-tune</summary>
          <label>
            Decline rate <output className="num">{Math.round(config.failureRate * 100)}%</output>
            <input
              type="range"
              min={0}
              max={100}
              defaultValue={config.failureRate * 100}
              key={`f-${config.failureRate}`}
              onPointerUp={(e) => void tune({ failureRate: Number(e.currentTarget.value) / 100 })}
              onKeyUp={(e) => void tune({ failureRate: Number(e.currentTarget.value) / 100 })}
            />
          </label>
          <label>
            Gateway latency <output className="num">{(config.latencyMs / 1000).toFixed(1)} s</output>
            <input
              type="range"
              min={0}
              max={25000}
              step={500}
              defaultValue={config.latencyMs}
              key={`l-${config.latencyMs}`}
              onPointerUp={(e) => void tune({ latencyMs: Number(e.currentTarget.value) })}
              onKeyUp={(e) => void tune({ latencyMs: Number(e.currentTarget.value) })}
            />
          </label>
          <label>
            Unacknowledged deliveries <output className="num">{Math.round(config.duplicateDeliveryRate * 100)}%</output>
            <input
              type="range"
              min={0}
              max={100}
              defaultValue={config.duplicateDeliveryRate * 100}
              key={`d-${config.duplicateDeliveryRate}`}
              onPointerUp={(e) => void tune({ duplicateDeliveryRate: Number(e.currentTarget.value) / 100 })}
              onKeyUp={(e) => void tune({ duplicateDeliveryRate: Number(e.currentTarget.value) / 100 })}
            />
          </label>
        </details>
      )}
    </section>
  );
}
