import { useState } from "react";
import { pingClient } from "@/api/transport";
import { create } from "@bufbuild/protobuf";
import { GreetRequestSchema } from "@gen/carpe_momentum/v1/ping_pb";

type Result =
  | { kind: "idle" }
  | { kind: "loading" }
  | { kind: "ok"; message: string; version: string; serverTime: string }
  | { kind: "err"; error: string };

export default function App() {
  const [name, setName] = useState("trader");
  const [result, setResult] = useState<Result>({ kind: "idle" });

  async function callGreet() {
    setResult({ kind: "loading" });
    try {
      const req = create(GreetRequestSchema, { name });
      const res = await pingClient.greet(req);
      setResult({
        kind: "ok",
        message: res.message,
        version: res.adapterVersion,
        serverTime: res.serverTime
          ? new Date(Number(res.serverTime.seconds) * 1000).toISOString()
          : "(none)",
      });
    } catch (e) {
      setResult({
        kind: "err",
        error: e instanceof Error ? e.message : String(e),
      });
    }
  }

  return (
    <>
      <h1>Carpe Momentum 2</h1>
      <p className="subtitle">Phase 0 — boundary smoke test</p>

      <div className="card">
        <label htmlFor="name">Your handle</label>
        <input
          id="name"
          type="text"
          value={name}
          onChange={(e) => setName(e.target.value)}
          disabled={result.kind === "loading"}
        />
        <button onClick={callGreet} disabled={result.kind === "loading"}>
          {result.kind === "loading" ? "Calling adapter…" : "Greet the adapter"}
        </button>

        {result.kind === "ok" && (
          <div className="result ok">
            {result.message}
            {"\n"}
            <span className="dim">
              adapter v{result.version} · server time {result.serverTime}
            </span>
          </div>
        )}
        {result.kind === "err" && (
          <div className="result err">
            Error: {result.error}
            {"\n"}
            <span className="dim">
              Is the adapter running? `cd adapter && dotnet run --project
              src/CarpeMomentum.Adapter`
            </span>
          </div>
        )}
      </div>
    </>
  );
}
