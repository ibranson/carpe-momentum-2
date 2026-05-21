# Carpe Momentum 2 — Specification

**Status:** Draft v0.1 — 2026-05-21
**Owner:** Project lead (solo build)
**Predecessor:** Carpe Momentum 1 (WPF + C# + IBKR.NET, shipped, moderate success)

---

## 1. Goals

CM2 is a single-trader momentum trading platform built around Ross Cameron's 5-Pillar Strategy, sourcing market data and order routing from Interactive Brokers (IBKR) via Trader Workstation (TWS).

Three concrete goals drive the design:

1. **Wean off Day Trade Dash (DTD).** Emulate the parts of DTD's output that are decision-useful; drop the rest.
2. **Tighten the Dashboard ↔ Trading-Window coupling.** A symbol promoted in the dashboard should land in the execution surface ("Stream Deck") with zero friction.
3. **Better visualizations for entry quality.** Move from DTD's binary "in the list" treatment to a continuous, per-pillar strength model that surfaces *why* a setup qualifies, *how fragile* it is, and *whether it's building or decaying*.

A successful v1 is one the operator runs alongside (then in place of) DTD for one trading week without missing functionality they wish they had.

---

## 2. Architecture

Two processes on the workstation, designed for future split across machines:

```
┌─ TWS Adapter Service (.NET 8, console / Windows service) ───────────┐
│   • IBKR official IBApi C# client → TWS / IB Gateway                │
│   • Owns: market data subscriptions, scanner subscriptions,         │
│     order routing, position state, P&L, news, halts                 │
│   • Exposes: gRPC + gRPC-Web (Kestrel)                              │
│   • Stores: SQLite — tick history, scanner snapshots, fills,        │
│     symbol metadata, news headlines                                 │
└──────────────────────────────────────────────────────────────────────┘
                          ▲  HTTP/2 (localhost today; LAN tomorrow)
                          │
┌─ CM2 UI (Tauri 2, Rust shell + React/TypeScript/Vite) ──────────────┐
│   • Multi-window: Dashboard, Stream Deck, Settings, [Charts pop-out]│
│   • Hosts TradingView Charting Library in each window's WebView2    │
│   • gRPC-Web client → TWS Adapter                                   │
│   • Global hotkeys (Tauri), per-window hotkeys (in-app)             │
│   • State: Zustand (UI), TanStack Query (data fetching), gRPC       │
│     server-streaming for ticks/scanner/quality updates              │
└──────────────────────────────────────────────────────────────────────┘
```

**Process boundary as first-class concern.** The gRPC service is a real process boundary from day one even when co-located — this preserves the option to move the TWS Adapter to a dedicated machine (see Goal 2's modular promise). Settings → Connection exposes the adapter endpoint; switching machines is a config change, not a code change.

**No shared in-memory state across the boundary.** The UI does not poke the adapter's internals; the adapter does not push UI concerns. All cross-process communication is gRPC.

---

## 3. Stack

| Layer | Choice | Why |
|---|---|---|
| **UI shell** | Tauri 2 | Native multi-window, ~15 MB binary, WebView2 = same engine WPF would use, first-class window/hotkey APIs |
| **UI app** | React + TypeScript + Vite | TradingView is JS-native (no interop tax), modern ecosystem (TanStack, shadcn, Tailwind), hot reload |
| **UI state** | Zustand + TanStack Query | Light global state for UI concerns; cached/streaming server state with built-in retry, dedup |
| **Charting** | TradingView Charting Library (free tier) | Industry standard; supports custom datafeed for IBKR ticks; requires application (1-2 wk approval) |
| **Adapter** | .NET 8 console / Windows service | Mature IBApi C# client; existing v1 expertise; high-perf gRPC server via Kestrel |
| **IBKR client** | Official IBApi C# (IBKR-published) | Canonical; full coverage; no third-party wrapper risk |
| **Wire protocol** | gRPC + gRPC-Web | Bidirectional streaming for ticks; typed contracts via .proto; built into ASP.NET Core (no Envoy proxy) |
| **Local store** | SQLite (Microsoft.Data.Sqlite) | Embedded, zero-ops, sufficient for single-user volumes |
| **Logging** | Serilog (.NET) + console structured logs (UI) | Structured, queryable, debug-friendly |
| **Packaging** | Tauri MSI + .NET single-file publish | Two side-by-side installs on Windows |

Rejected alternatives are documented in `/memory/project_cm2_stack.md`.

---

## 4. v1 Scope

v1 ships **the minimum surface needed to abandon DTD for a trading week** — not feature parity, not feature ambition.

### Dashboard (v1)
- **5-Pillars Scan** panel with per-pillar strength bars, Setup Quality column, trend arrow, catalyst chip, recent-crossover glyph
- **Running Up** panel — momentum alert stream; configurable audible alert
- **5-Pillars Timeline** — replacement for DTD's "5-Pillars Alert" daily list; symbol-vs-time ribbon of qualifying symbols colored by Setup Quality
- **Stock Quote** card — symbol context with flag/sector glyphs; populated by focus events
- **Focused-symbol pub/sub** — clicking any symbol in any panel refocuses Stock Quote, the active chart, and the Stream Deck

### Stream Deck (v1)
- **Focused-symbol header** — name + flag/sector glyphs + halt indicator (with countdown) + live 5-pillar readout + Setup Quality + sparkline
- **L2 / Order Book** with size heatmap
- **Time & Sales** with size-aggregation toggle (hide odd lots)
- **Trade Ticket** — Buy/Sell, shares (price-band default), manual stop field, market/limit/trail order types, hotkey-first
- **Position & P&L** — open position, avg cost, unrealized $/% , day P&L, Exit/Flatten hotkeys
- **Watchlist column** — symbol + live %gain + RVOL + status dot (in-position / recent / quiet)
- **Daily Risk Advisory** strip — soft limit bar, color-graded, advisory only
- **Exit-side nudges** — visual always, audible only when position > 0

### Charts (v1)
- TradingView Charting Library with **custom datafeed** sourced from TWS Adapter
- Default intervals: **10-second and 5-minute**
- Always-on overlays: VWAP, MACD, Volume, EMAs (9 + 20)
- Pattern annotations (DENSE by default): **VWAP Reclaim** + **First Pullback** + Bull Flag Breakout + HoD/Pre-market High lines + Halt→Resume markers + Catalyst markers
- Setup Quality sparkline strip below the volume pane (persistent)
- Each pattern toggleable in Settings → Pattern Detection

### Settings (v1)
- 5-Pillar Thresholds (with canonical defaults — see `/memory/project_five_pillars.md`)
- 5-Pillar Weights (equal-weighted by default)
- Position Sizing — price-band → default shares
- Risk Advisory — daily soft limit, yellow/red thresholds
- Alerts — sound per source (file picker for .wav)
- Hotkeys — rebindable
- News Providers — IBKR (on); Benzinga Pro (placeholder, off)
- Display — theme, font size, watchlist columns, **Setup Quality color scale** (R→Y→G default; viridis available)
- Order Defaults — default type, trail offset default, TIF default, default share count by price band
- Pattern Detection — per-pattern toggles + tunable thresholds
- Connection — TWS host/port, gRPC adapter endpoint

### Explicitly out of v1
- Backtesting / strategy simulation
- Replay mode
- Multi-account routing
- Options trading
- Benzinga or other paid news integrations (interface present, implementation deferred)
- Bracket orders by default (available, but no UI shortcut)
- Hardware Stream Deck integration
- Mobile companion
- Cloud sync of settings

---

## 5. Phased Build Plan

Each phase is ~2-3 weeks of evenings/weekends. Each ends with a runnable artifact.

### Phase 0 — Foundations (Week 1)
- Repo skeleton: `/adapter` (.NET solution), `/ui` (Tauri+Vite), `/proto` (gRPC .proto files), `/docs`
- Toolchain: .NET 8 SDK, Rust+Cargo, Node+pnpm, Tauri CLI, Buf or grpc-tools for protos
- Hello-world: Tauri window calls a single gRPC method on the adapter; adapter logs and replies. Proves the boundary.
- TradingView Charting Library application submitted (this can take 1-2 weeks — start the clock now)

### Phase 1 — TWS Adapter Core (Weeks 2-4)
- IBApi client lifecycle (connect, reconnect, account sub)
- Market data subscriptions: real-time bid/ask/last + tick-by-tick
- Historical bars for chart datafeed (10s + 5m + 1m + 1d resolutions)
- Scanner subscriptions (IBKR's built-in market scanners — top gainers, top losers, etc., as a starting feed to filter through the 5-Pillar evaluator)
- 5-Pillar Evaluator service — takes a symbol's live data, returns per-pillar strengths + Setup Quality
- News subscription
- gRPC service contracts (see §6) — get them stable early; UI builds against them
- SQLite schema + write path for tick history and scanner snapshots

### Phase 2 — Dashboard MVP (Weeks 5-6)
- Tauri main window = Dashboard layout
- 5-Pillars Scan panel wired to adapter's quality-stream gRPC
- Running Up panel wired to adapter's momentum-alerts stream
- Stock Quote card with focus-event subscription
- Focused-symbol global state (Zustand) + pub/sub to all panels
- Configurable audible alerts (Web Audio API; user-supplied .wav via Settings)

### Phase 3 — Charts (Weeks 7-8)
- TradingView Charting Library integrated (requires approval received by now)
- Custom Datafeed implementation backed by adapter's historical-bars + real-time-bars gRPC
- VWAP / EMA / MACD / Volume overlays
- Pattern annotation engine (VWAP Reclaim + First Pullback first — others can follow)
- Setup Quality sparkline strip
- Chart-in-window vs chart-popout (Tauri secondary window)

### Phase 4 — Stream Deck (Weeks 9-11)
- Stream Deck secondary Tauri window
- L2 / Order Book (subscribes to deep-market-data via adapter)
- Trade Ticket with hotkey bindings (Tauri global hotkeys + in-app)
- Position panel with live P&L
- Watchlist column with status dots
- Order routing via adapter's order-submit gRPC
- Trail-stop one-keystroke action
- Exit-side nudges (pulse, audible-if-position)

### Phase 5 — Settings & Polish (Week 12)
- Settings secondary Tauri window with all sections wired to persisted store (SQLite or local JSON)
- 5-Pillars Timeline panel
- Halt indicator + countdown
- Color scale switcher
- Hotkey rebinding UI
- Smoke-test "real trading week" with paper account

### Phase 6 — Stabilization (Week 13+)
- Bug-bash with live (paper) trading
- Performance: ensure tick rates don't choke at 50+ symbols subscribed
- Crash recovery: adapter reconnect, UI session restore
- Documentation: a short operator guide so you can remember the hotkeys after a vacation

---

## 6. Module specifications

### 6.1 TWS Adapter — gRPC service contracts (initial sketch)

```proto
syntax = "proto3";
package carpe_momentum.v1;

service MarketDataService {
  rpc StreamQuotes(SymbolList) returns (stream QuoteUpdate);
  rpc StreamLevel2(SymbolRequest) returns (stream Level2Update);
  rpc StreamTimeAndSales(SymbolRequest) returns (stream TradePrint);
  rpc GetHistoricalBars(BarRequest) returns (BarSeries);
  rpc StreamRealTimeBars(BarRequest) returns (stream Bar);
}

service ScannerService {
  // Combines IBKR scanner outputs + 5-Pillar evaluation.
  rpc StreamQualifyingSymbols(ScannerSubscription) returns (stream QualityUpdate);
  // QualityUpdate contains: symbol, 5 pillar strengths, aggregate Q, trend, catalyst.
  rpc StreamRunningUp(Empty) returns (stream MomentumAlert);
  rpc StreamHaltEvents(Empty) returns (stream HaltEvent);
}

service OrderService {
  rpc SubmitOrder(OrderRequest) returns (OrderAck);
  rpc CancelOrder(OrderId) returns (OrderAck);
  rpc StreamOrderUpdates(Empty) returns (stream OrderStatus);
  rpc StreamPositions(Empty) returns (stream PositionUpdate);
  rpc StreamDailyPnL(Empty) returns (stream PnLUpdate);
}

service NewsService {
  rpc StreamHeadlines(SymbolList) returns (stream NewsItem);
  rpc GetSymbolMetadata(SymbolRequest) returns (SymbolMetadata);
  // metadata: name, country, exchange, sector, industry, float, etc.
}

service SettingsService {
  rpc GetPillarConfig(Empty) returns (PillarConfig);
  rpc UpdatePillarConfig(PillarConfig) returns (Empty);
  // ... other config domains
}
```

Contracts are sketches — refine as Phase 1 progresses. The principle is **server-streaming for everything that updates** (ticks, scanner, quality, positions, P&L) and **unary for commands and one-shot reads**.

### 6.2 UI — window layout

- **Main window** (Dashboard): 5-Pillars Scan, Running Up, 5-Pillars Timeline, Stock Quote
- **Stream Deck window**: focused symbol execution surface
- **Settings window**: tabbed sections
- **Chart pop-out windows** (optional, can pop out from inline): one per symbol the user pops; each is a Tauri secondary window with its own TradingView instance

All windows subscribe to the focused-symbol state via Tauri's IPC event bus. Re-focusing one symbol in the dashboard refocuses Stock Quote, the pop-out chart (if any), and the Stream Deck.

### 6.3 UI — focused-symbol pub/sub

```
DashboardPanel.onSymbolClick(sym) → emit("focus", sym)
ChartWindow.on("focus", sym) → load chart for sym
StockQuoteCard.on("focus", sym) → fetch metadata + recent news
StreamDeckWindow.on("focus", sym) → resubscribe L2, T&S, refocus ticket
```

Implementation: Tauri event bus for cross-window; Zustand store for within-window state.

---

## 7. Data Model (key entities)

- **Symbol** — ticker, exchange, country, sector, industry, float, avg daily volume, name
- **Quote** — symbol, ts, bid, ask, last, bid_size, ask_size, volume_today
- **Bar** — symbol, ts, resolution, open, high, low, close, volume, vwap
- **Level2Level** — symbol, side, level, price, size
- **TradePrint** — symbol, ts, price, size, aggressor (buy/sell/unknown)
- **PillarStrengths** — symbol, ts, price_score, gain_score, float_score, rvol_score, catalyst_score, aggregate_q
- **MomentumAlert** — symbol, ts, kind (running_up | crossover_up | crossover_down)
- **NewsItem** — id, symbol(s), ts, source, headline, url, category (earnings, fda, contract, m&a, sector, other)
- **HaltEvent** — symbol, ts, kind (halt | resume), reason_code, est_resume_ts
- **Order** — id, symbol, side, qty, type, limit_price, stop_price, trail_offset, tif, status
- **Position** — symbol, qty, avg_cost, market_price, unrealized, realized_today
- **PillarConfig** — per-pillar thresholds, per-pillar weights
- **AlertConfig** — per-source sound file paths, per-source enable flags
- **HotkeyBinding** — action → key combo

---

## 8. Cross-cutting concerns

**Hotkeys.** All execution actions are hotkey-bound and rebindable. Tauri provides global hotkeys (work when window not focused — used for emergency Flatten); in-app hotkeys when window is focused.

**Audible alerts.** Web Audio API in the UI; sounds loaded from user-supplied `.wav` files via Settings. Each source (Running Up, 5-Pillars crossover, Halt, Exit-side nudge) has its own slot. Exit-side nudges check `position.qty > 0` before firing audio.

**State persistence.** Settings + watchlist persisted to local JSON in Tauri's app-data directory. Tick history + scanner snapshots + fills go to SQLite in the adapter.

**Observability.** Adapter logs (Serilog → rolling files); UI logs (console + Tauri's log plugin → rolling files). Both timestamped, structured, with a session ID for correlation.

**Reconnection.** Adapter reconnects to TWS with exponential backoff. UI reconnects to adapter the same way. Connection status visible in a small footer on every window.

**Time zones.** All timestamps stored UTC; displayed in user's local time. Trading-session-aware components (pre-market, regular, after-hours) use Eastern time regardless of operator location.

---

## 9. Open questions / decisions deferred

| # | Question | When to decide |
|---|---|---|
| 1 | Exact tick-buffering strategy in adapter for 10s bar synthesis (every tick vs. coalesce) | Phase 1 |
| 2 | TradingView Charting Library vs. Advanced Charts — only matters if free tier license terms prove restrictive | Once application processed |
| 3 | Whether the 5-Pillars Timeline becomes a horizontal swimlane or a vertical feed | Phase 5 (after using v0) |
| 4 | Whether watchlist persists across sessions automatically or per explicit save | Phase 4 |
| 5 | Settings storage format: JSON file vs. SQLite vs. Tauri's built-in store | Phase 5 |
| 6 | Whether Benzinga Pro stays in v1 settings as a disabled placeholder or is removed until ready | Phase 5 |

---

## 10. Pre-v1 action items (operator)

- [ ] **Apply for TradingView Charting Library access** at https://www.tradingview.com/charting-library/ — approval takes 1-2 weeks
- [ ] **Confirm IBKR market data subscriptions** cover the universe you scan (small-cap, OTC if relevant, level 2)
- [ ] **Confirm TWS API is enabled** in TWS → Configure → API → Settings (and trusted IP if running adapter remotely later)
- [ ] **Decide paper vs. live account** for development (strongly recommend paper through Phase 5)
- [ ] **Collect alert sound files** (.wav) you want to use — one per alert source, distinct enough to identify by ear

---

## Appendix A — Glossary

- **5 Pillars**: Ross Cameron's 5 criteria for momentum candidates — Price, % Gain, Float, RVOL, Catalyst
- **Setup Quality**: 0-100 weighted-average score across pillar strengths
- **RVOL**: Relative volume — today's cumulative volume / average cumulative volume at this time of day
- **HoD / LoD**: High / Low of Day
- **VWAP**: Volume-Weighted Average Price
- **LULD**: Limit Up / Limit Down — SEC-mandated volatility halts
- **OCA**: One-Cancels-All — order group linkage where one fill cancels siblings
- **TIF**: Time In Force — DAY, GTC, IOC, FOK
- **TWS**: Trader Workstation (IBKR's desktop client; the adapter speaks to its API)
- **IB Gateway**: Headless alternative to TWS; same API, no chart UI

---

## Appendix B — Why not Day Trade Dash?

DTD works. The reasons CM2 is worth building:

1. **Subscription cost** redirects to better data sourcing (Benzinga Pro, deeper L2).
2. **Per-pillar strength visualization** is genuinely novel and gives an entry-timing edge DTD doesn't offer.
3. **Dashboard ↔ execution coupling** — DTD's link-by-symbol works but is one-way; CM2 builds bidirectional focus.
4. **Audible alert customization** — DTD's uniform alerts force visual scanning; CM2's per-source sounds make alerts identifiable by ear.
5. **Configurability** — every threshold tunable; DTD's filters are blunter.
6. **Ownership** — CM2 evolves at the operator's pace, not a vendor's.
