# DevOps Dashboard — Design System

Status: **normative**. Every page uses these tokens and components. New CSS goes in `app.css` under the matching section; no inline `style=""` except computed widths (bars, sparklines).
Dark is the primary theme; light must pass the same contrast checks (body text ≥ 7:1, muted ≥ 4.5:1, subtle ≥ 3:1 for non-essential text only).

---

## 1. Tokens

```css
:root {
  /* Surfaces (dark default) */
  --surface-0: #0d1117;   /* page background */
  --surface-1: #151b23;   /* panel */
  --surface-2: #1c232c;   /* raised: table hover, inputs, kpi tile, code */
  --surface-3: #252e39;   /* active/pressed, tooltip */
  --border:        #2a333e;
  --border-strong: #3a4552;
  --text:   #e6ebf1;
  --muted:  #9aa6b3;      /* secondary text, labels */
  --subtle: #6b7785;      /* "same", "—", hints; never essential info */

  --accent:        #5b9bff;
  --accent-bg:     #172a46;
  --accent-fg-on:  #0d1117;  /* text on solid accent */

  /* Semantic: fg = text/icon/rail, bg = pill/banner fill */
  --ok-fg: #4cc26b;   --ok-bg: #12301c;
  --warn-fg: #e3b341; --warn-bg: #33290d;
  --bad-fg: #ff6b6b;  --bad-bg: #3b1618;
  --info-fg: #58a6ff; --info-bg: #122a46;
  --neutral-fg: #9aa6b3; --neutral-bg: #222a34;

  /* Maturity tiers */
  --tier-gold-fg: #e5b94e;   --tier-gold-bg: #33290d;
  --tier-silver-fg: #b8c2cc; --tier-silver-bg: #262d36;
  --tier-bronze-fg: #d08a5c; --tier-bronze-bg: #35200f;
  --tier-none-fg: #6b7785;   --tier-none-bg: transparent;

  /* Environment tiers (column header dot + env pill) */
  --env-dev: #8b949e;       /* team / dev */
  --env-staging: #58a6ff;
  --env-sim: #b083f0;
  --env-prod: #f0883e;

  /* Spacing (4px base) */
  --sp-1: 4px; --sp-2: 8px; --sp-3: 12px; --sp-4: 16px; --sp-5: 24px; --sp-6: 32px; --sp-7: 48px;

  /* Radii */
  --r-sm: 4px;    /* pills, inputs inside tables, code */
  --r-md: 6px;    /* buttons, inputs, kpi */
  --r-lg: 10px;   /* panels, banners */
  --r-full: 999px;/* dots, count pill only */

  /* Type */
  --font-ui: "Segoe UI Variable Text", "Segoe UI", system-ui, -apple-system, Roboto, sans-serif;
  --font-mono: "Cascadia Mono", "Cascadia Code", Consolas, ui-monospace, monospace;
  --fs-xs: 11px;  /* pills, table headers */
  --fs-sm: 12px;  /* meta, sub, ago */
  --fs-md: 14px;  /* body, table cells */
  --fs-lg: 16px;  /* panel title */
  --fs-xl: 20px;  /* page title */
  --fs-kpi: 24px; /* kpi value */
  --lh-tight: 1.25; --lh-body: 1.45;
  --fw-regular: 400; --fw-medium: 500; --fw-semibold: 600;

  /* Elevation */
  --shadow-1: none;
  --shadow-pop: 0 6px 24px rgba(0,0,0,.45);   /* tooltip, menus */
  --focus-ring: 0 0 0 2px var(--surface-0), 0 0 0 4px var(--accent);

  /* Z-index */
  --z-sticky: 10;   /* table thead */
  --z-topbar: 20;
  --z-pop: 30;      /* tooltip, dropdown */
  --z-modal: 40;
  --z-toast: 50;

  /* Layout */
  --content-max: 1600px;
  --prose-max: 72ch;
}

:root[data-theme="light"] { color-scheme: light;
  --surface-0: #f5f7fa; --surface-1: #ffffff; --surface-2: #f0f3f6; --surface-3: #e4e9ef;
  --border: #d8dee5; --border-strong: #b9c2cc;
  --text: #1b222b; --muted: #57626e; --subtle: #808b97;
  --accent: #1f62d6; --accent-bg: #e6eeff; --accent-fg-on: #ffffff;
  --ok-fg: #1a7f37; --ok-bg: #dff3e5;  --warn-fg: #8a5d00; --warn-bg: #fff1cc;
  --bad-fg: #c62828; --bad-bg: #fde6e6; --info-fg: #0b63c9; --info-bg: #e3effd;
  --neutral-fg: #57626e; --neutral-bg: #eceff3;
  --tier-gold-fg: #8a6300; --tier-gold-bg: #fbefc9;
  --tier-silver-fg: #56616d; --tier-silver-bg: #e9edf1;
  --tier-bronze-fg: #94491f; --tier-bronze-bg: #f8e3d4;
  --env-dev: #6e7781; --env-staging: #0b63c9; --env-sim: #8250df; --env-prod: #c4561a;
  --shadow-1: 0 1px 2px rgba(16,24,40,.06); --shadow-pop: 0 6px 24px rgba(16,24,40,.18);
}
@media (prefers-color-scheme: light) {
  :root:not([data-theme="dark"]) { /* paste the [data-theme=light] declarations verbatim (CSS cannot share them); keep both in sync */ }
}
:root { color-scheme: dark; }
html, body { background: var(--surface-0); color: var(--text); font: var(--fw-regular) var(--fs-md)/var(--lh-body) var(--font-ui); }
```

Rule: components reference **only** tokens. No hex values outside the token blocks.

---

## 2. Components

### 2.1 Page header (mandatory on every page)
One per page. Title = noun ("Now", "Repo · core-db"). Headline = one computed sentence answering "is anything wrong?".
```html
<header class="page-head">
  <div class="page-head__text">
    <h1>Now</h1>
    <p class="page-head__headline"><strong class="tone-bad">2 repos</strong> have main red · 0 approvals waiting · synced <Ago/></p>
  </div>
  <div class="page-head__actions"><button class="btn btn--secondary btn--sm">Refresh</button></div>
</header>
```
```css
.page-head { display:flex; align-items:flex-end; gap:var(--sp-4); margin:0 0 var(--sp-5); flex-wrap:wrap; }
.page-head__text { flex:1; min-width:0; }
.page-head h1 { font-size:var(--fs-xl); font-weight:var(--fw-semibold); line-height:var(--lh-tight); margin:0; }
.page-head__headline { margin:var(--sp-1) 0 0; color:var(--muted); max-width:var(--prose-max); }
.page-head__actions { display:flex; gap:var(--sp-2); }
```

### 2.2 Panel
```html
<section class="panel [is-empty]">
  <header class="panel__head"><h2>Needs attention</h2><span class="pill pill--count pill--bad">2</span>
    <span class="panel__meta">…</span></header>
  <div class="panel__body">…</div>
</section>
```
```css
.panel { background:var(--surface-1); border:1px solid var(--border); border-radius:var(--r-lg); box-shadow:var(--shadow-1); padding:var(--sp-4); }
.panel__head { display:flex; align-items:center; gap:var(--sp-2); margin-bottom:var(--sp-3); min-height:24px; }
.panel__head h2 { font-size:var(--fs-lg); font-weight:var(--fw-semibold); margin:0; }
.panel__meta { margin-left:auto; color:var(--muted); font-size:var(--fs-sm); }
/* Empty panel collapses to a single 40px line: title + empty text inline */
.panel.is-empty { padding:var(--sp-2) var(--sp-4); background:transparent; border-style:dashed; }
.panel.is-empty .panel__head { margin:0; }
.panel.is-empty .panel__body { display:none; }
.panel.is-empty .panel__head::after { content:attr(data-empty); color:var(--subtle); font-size:var(--fs-sm); }
.stack { display:flex; flex-direction:column; gap:var(--sp-4); }
.grid-2 { display:grid; grid-template-columns:repeat(auto-fit,minmax(min(440px,100%),1fr)); gap:var(--sp-4); }
.grid-3 { display:grid; grid-template-columns:repeat(auto-fit,minmax(min(300px,100%),1fr)); gap:var(--sp-4); }
```
Usage: `<section class="panel is-empty" data-empty="Nothing running">`. Empty panels sort to the bottom of the page.

### 2.3 KPI tile + row
```html
<div class="kpi-row">
  <div class="kpi kpi--bad"><div class="kpi__label">Main/CI failures <span class="kpi__window">24h</span></div>
    <div class="kpi__value">3</div><div class="kpi__sub">vs 1 prev 24h</div></div>
</div>
```
```css
.kpi-row { display:grid; grid-template-columns:repeat(auto-fill,minmax(160px,1fr)); gap:var(--sp-3); }
.kpi { background:var(--surface-2); border-radius:var(--r-md); padding:var(--sp-3); border-left:3px solid transparent; min-height:84px; }
.kpi__label { font-size:var(--fs-sm); color:var(--muted); display:flex; gap:var(--sp-1); }
.kpi__window { color:var(--subtle); }
.kpi__value { font-size:var(--fs-kpi); font-weight:var(--fw-semibold); line-height:1.2; font-variant-numeric:tabular-nums; margin-top:var(--sp-1); }
.kpi__value small { font-size:var(--fs-md); color:var(--muted); font-weight:var(--fw-regular); margin-left:2px; } /* unit */
.kpi__sub { font-size:var(--fs-sm); color:var(--muted); }
.kpi--bad { border-left-color:var(--bad-fg); } .kpi--bad .kpi__value { color:var(--bad-fg); }
.kpi--warn { border-left-color:var(--warn-fg); }
.kpi--ok .kpi__value { color:var(--text); } /* ok values stay neutral; never green zeros */
```
Max 6 tiles per row. Value is a number + unit; never text sentences.

### 2.4 Pill (the only badge)
```css
.pill { display:inline-flex; align-items:center; gap:4px; height:20px; padding:0 6px; border-radius:var(--r-sm);
  font:var(--fw-semibold) var(--fs-xs)/1 var(--font-ui); white-space:nowrap; vertical-align:middle;
  background:var(--neutral-bg); color:var(--neutral-fg); max-width:22ch; overflow:hidden; text-overflow:ellipsis; }
.pill--ok   { background:var(--ok-bg);   color:var(--ok-fg); }
.pill--warn { background:var(--warn-bg); color:var(--warn-fg); }
.pill--bad  { background:var(--bad-bg);  color:var(--bad-fg); }
.pill--info { background:var(--info-bg); color:var(--info-fg); }
/* severity = same tones: critical/high→bad, medium→warn, low→info, note→neutral */
.pill--gold   { background:var(--tier-gold-bg);   color:var(--tier-gold-fg); }
.pill--silver { background:var(--tier-silver-bg); color:var(--tier-silver-fg); }
.pill--bronze { background:var(--tier-bronze-bg); color:var(--tier-bronze-fg); }
.pill--none   { background:transparent; color:var(--tier-none-fg); box-shadow:inset 0 0 0 1px var(--border); }
.pill--ref    { font-weight:var(--fw-medium); background:var(--surface-3); color:var(--muted); max-width:14ch; } /* branch/tag */
.pill--ref.is-nondefault { background:var(--warn-bg); color:var(--warn-fg); }
.pill--env::before { content:""; width:6px; height:6px; border-radius:var(--r-full); background:var(--env-c); }
.pill--env { --env-c:var(--env-dev); }
.pill--env.env-staging { --env-c:var(--env-staging); } .pill--env.env-sim { --env-c:var(--env-sim); } .pill--env.env-prod { --env-c:var(--env-prod); }
.pill--count { min-width:20px; justify-content:center; border-radius:var(--r-full); font-variant-numeric:tabular-nums; }
.pill .pill__sub { font-weight:var(--fw-regular); opacity:.8; } /* e.g. tier provenance */
```
Exactly one pill height (20px) everywhere. Pills carry a title attribute with the full text when truncated.

### 2.5 Chip (components, removable filters)
```css
.chip { display:inline-flex; align-items:center; gap:4px; height:22px; padding:0 8px; border-radius:var(--r-sm);
  border:1px solid var(--border); background:transparent; color:var(--text); font-size:var(--fs-sm); max-width:28ch;
  overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.chip__kind { color:var(--subtle); font-size:var(--fs-xs); text-transform:uppercase; } /* "DB", "AKS", "SF" — text, not emoji */
.chip-list { display:flex; flex-wrap:wrap; gap:var(--sp-1); }
```
Chip = an object (component, filter). Pill = a state/attribute. Never swap.

### 2.6 Table
```css
.table-wrap { overflow-x:auto; max-width:100%; }
.table-wrap.is-scroll { max-height:70vh; overflow:auto; }
table.t { width:100%; border-collapse:separate; border-spacing:0; font-size:var(--fs-md); }
table.t th, table.t td { padding:var(--sp-2) var(--sp-3); border-bottom:1px solid var(--border); text-align:left; vertical-align:top; }
table.t thead th { position:sticky; top:0; z-index:var(--z-sticky); background:var(--surface-1);
  font-size:var(--fs-xs); font-weight:var(--fw-semibold); color:var(--muted); text-transform:uppercase; letter-spacing:.04em; white-space:nowrap; }
table.t tbody tr:hover { background:var(--surface-2); }
table.t.compact th, table.t.compact td { padding:var(--sp-1) var(--sp-2); }
table.t .num   { text-align:right; font-variant-numeric:tabular-nums; white-space:nowrap; }
table.t .nowrap{ white-space:nowrap; }
table.t .trunc { max-width:0; width:30%; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; } /* add title="full" */
table.t .wrap  { overflow-wrap:anywhere; min-width:16ch; }
.clamp-2 { display:-webkit-box; -webkit-line-clamp:2; -webkit-box-orient:vertical; overflow:hidden; overflow-wrap:anywhere; }
.clamp-2.is-open { -webkit-line-clamp:unset; }
.clamp-toggle { font-size:var(--fs-sm); color:var(--accent); background:none; border:0; padding:0; cursor:pointer; }
table.t th.sortable { cursor:pointer; } table.t th[aria-sort] { color:var(--text); }
table.t tr.is-bad td:first-child { box-shadow:inset 3px 0 0 var(--bad-fg); }
```
Expand is click (toggle `.is-open`), not hover. Every column has exactly one of `.num | .nowrap | .trunc | .wrap`.

### 2.7 Version cell
```html
<td class="ver-cell [is-same|is-drift|is-empty]">
  <div class="ver-cell__main"><span class="pill pill--ref [is-nondefault]" title="feature/1234-x">feature/1234…</span>
    <span class="ver">20261126.1</span></div>
  <div class="ver-cell__meta"><Ago/> · <span title="commits since build">+3</span></div></td>
```
```css
.ver-cell { min-width:128px; }
.ver-cell__main { display:flex; align-items:center; gap:var(--sp-1); }
.ver { font-variant-numeric:tabular-nums; font-weight:var(--fw-medium); }    /* UI font, not mono */
.ver-cell__meta { font-size:var(--fs-sm); color:var(--muted); }
.ver-cell.is-same .ver-cell__main { color:var(--subtle); } /* renders "= same" text, no pill */
.ver-cell.is-drift .ver { color:var(--warn-fg); }          /* + ⚠ icon with tooltip */
.ver-cell.is-empty { color:var(--subtle); }                 /* renders "—" */
```
Default branch (main/master) → **no pill**. Pill only when the ref is non-default.

### 2.8 Ago / time
```css
.ago { font-size:var(--fs-sm); color:var(--muted); white-space:nowrap; font-variant-numeric:tabular-nums; }
.ago.is-stale { color:var(--warn-fg); }
```
`<time class="ago" datetime="ISO" title="Thu 26 Nov 20:53 (local)">3h ago</time>`. Format: `just now` (<1m), `Nm`, `Nh`, `Nd` (<60d), then `d MMM` (same year) / `d MMM yyyy`. In tables write `3h`; in prose and headlines write `3h ago`.

### 2.9 Empty state line
```css
.empty { color:var(--subtle); font-size:var(--fs-sm); padding:var(--sp-2) 0; }
```
One line, no emoji, no illustration. Inside a whole-empty panel use `.panel.is-empty` instead.

### 2.10 Buttons
```css
.btn { display:inline-flex; align-items:center; gap:var(--sp-1); height:32px; padding:0 var(--sp-3); border-radius:var(--r-md);
  font:var(--fw-medium) var(--fs-md)/1 var(--font-ui); border:1px solid transparent; cursor:pointer; white-space:nowrap; }
.btn:focus-visible { outline:none; box-shadow:var(--focus-ring); }
.btn:disabled { opacity:.5; cursor:not-allowed; }
.btn--primary   { background:var(--accent); color:var(--accent-fg-on); }
.btn--secondary { background:var(--surface-2); color:var(--text); border-color:var(--border); }
.btn--ghost     { background:transparent; color:var(--muted); } .btn--ghost:hover { background:var(--surface-2); color:var(--text); }
.btn--danger    { background:transparent; color:var(--bad-fg); border-color:var(--bad-fg); }
.btn--primary:hover { filter:brightness(1.08); } .btn--secondary:hover { border-color:var(--border-strong); }
.btn--sm { height:24px; padding:0 var(--sp-2); font-size:var(--fs-sm); }
.btn--lg { height:40px; padding:0 var(--sp-4); }
```
One primary per view. Danger never solid-filled.

### 2.11 Form controls
```css
.input, .select, .textarea { height:32px; padding:0 var(--sp-2); border:1px solid var(--border); border-radius:var(--r-md);
  background:var(--surface-2); color:var(--text); font:inherit; }
.textarea { height:auto; padding:var(--sp-2); min-height:80px; }
.input:focus-visible, .select:focus-visible, .textarea:focus-visible { outline:none; border-color:var(--accent); box-shadow:var(--focus-ring); }
.field { display:flex; flex-direction:column; gap:var(--sp-1); } .field > label { font-size:var(--fs-sm); color:var(--muted); }
.filters { display:flex; gap:var(--sp-2); align-items:center; flex-wrap:wrap; margin-bottom:var(--sp-3); }
.check { display:inline-flex; gap:var(--sp-1); align-items:center; font-size:var(--fs-sm); color:var(--muted); }
input[type=checkbox] { accent-color:var(--accent); }
```

### 2.12 Tabs
```css
.tabs { display:flex; gap:var(--sp-1); border-bottom:1px solid var(--border); margin:0 0 var(--sp-4); overflow-x:auto; }
.tabs a { padding:var(--sp-2) var(--sp-3); color:var(--muted); font-weight:var(--fw-medium); border-bottom:2px solid transparent; margin-bottom:-1px; white-space:nowrap; }
.tabs a:hover { color:var(--text); text-decoration:none; }
.tabs a.is-active { color:var(--text); border-bottom-color:var(--accent); }
.tabs .pill--count { margin-left:var(--sp-1); }
```

### 2.13 Info tooltip
```css
.info-tip { position:relative; display:inline-flex; width:16px; height:16px; align-items:center; justify-content:center;
  border-radius:var(--r-full); color:var(--subtle); font-size:var(--fs-xs); cursor:help; border:1px solid var(--border); }
.info-tip:hover, .info-tip:focus-visible { color:var(--text); }
.info-tip__body { position:absolute; z-index:var(--z-pop); top:calc(100% + 6px); left:50%; transform:translateX(-50%);
  width:max-content; max-width:320px; padding:var(--sp-2) var(--sp-3); background:var(--surface-3); color:var(--text);
  border-radius:var(--r-md); box-shadow:var(--shadow-pop); font-size:var(--fs-sm); font-weight:var(--fw-regular); text-transform:none; display:none; }
.info-tip:hover .info-tip__body, .info-tip:focus-within .info-tip__body { display:block; }
```
Must be focusable (`tabindex="0"`). Explanations of ranking/legend go here, not in `.panel__meta`.

### 2.14 Sparkline & bar
```css
.spark { width:96px; height:24px; vertical-align:middle; color:var(--accent); } /* stroke: currentColor, 1.5px; no axes */
.spark--lg { width:240px; height:48px; }
.spark .spark__last { fill:currentColor; } /* 3px dot on last point */
.bar { height:6px; min-width:60px; background:var(--neutral-bg); border-radius:var(--r-sm); overflow:hidden; }
.bar > span { display:block; height:100%; background:var(--accent); }
```
A sparkline always sits next to its current value + window label: `42 min · 30d ▁▃▅`.

### 2.15 Attention item (severity rail)
```html
<li class="att att--bad"><div class="att__body"><a class="att__title" href="repo/x">main red for 27d · hornbill-bot</a>
  <div class="att__detail">hornbill-support-ci #1.0.53</div></div>
  <div class="att__side"><Ago/><a href="…">Open in ADO ↗</a></div></li>
```
```css
.att-list { list-style:none; margin:0; padding:0; display:flex; flex-direction:column; gap:var(--sp-2); }
.att { display:grid; grid-template-columns:1fr auto; gap:var(--sp-3); padding:var(--sp-2) var(--sp-3);
  background:var(--surface-2); border-radius:var(--r-md); box-shadow:inset 3px 0 0 var(--att-c, var(--border-strong)); }
.att--bad { --att-c:var(--bad-fg); } .att--warn { --att-c:var(--warn-fg); } .att--you { --att-c:var(--accent); } .att--info { --att-c:var(--neutral-fg); }
.att__title { font-weight:var(--fw-semibold); color:var(--text); } .att__title:hover { color:var(--accent); }
.att__detail { font-size:var(--fs-sm); color:var(--muted); overflow-wrap:anywhere; }
.att__side { display:flex; flex-direction:column; align-items:flex-end; gap:2px; font-size:var(--fs-sm); white-space:nowrap; }
```
Rail is the severity signal; no extra red dot icon, no full-row tint.

### 2.16 Banner & status dot
```css
.banner { display:flex; gap:var(--sp-2); padding:var(--sp-2) var(--sp-3); border-radius:var(--r-lg); margin-bottom:var(--sp-4); background:var(--info-bg); color:var(--text); box-shadow:inset 3px 0 0 var(--info-fg); }
.banner--bad { background:var(--bad-bg); box-shadow:inset 3px 0 0 var(--bad-fg); } .banner--warn { background:var(--warn-bg); box-shadow:inset 3px 0 0 var(--warn-fg); }
.dot { display:inline-block; width:8px; height:8px; border-radius:var(--r-full); background:var(--neutral-fg); }
.dot--ok{background:var(--ok-fg)} .dot--warn{background:var(--warn-fg)} .dot--bad{background:var(--bad-fg)} .dot--info{background:var(--info-fg)}
```

### 2.17 Text utilities (the only ones allowed)
```css
.tone-ok{color:var(--ok-fg)} .tone-warn{color:var(--warn-fg)} .tone-bad{color:var(--bad-fg)} .tone-muted{color:var(--muted)} .tone-subtle{color:var(--subtle)}
.mono { font-family:var(--font-mono); font-size:.92em; }
.text-sm { font-size:var(--fs-sm); } .nums { font-variant-numeric:tabular-nums; }
.row { display:flex; gap:var(--sp-2); align-items:center; flex-wrap:wrap; } .grow { flex:1; min-width:0; }
.mt-2{margin-top:var(--sp-2)} .mt-4{margin-top:var(--sp-4)}  /* replace every inline style margin */
```

---

## 3. Usage rules

**Layout & density**
- Shell: topbar 48px; nav = `Now · Repos · Improve` + gear icon (Admin) right-aligned. Content max 1600px, padding 16px/24px.
- Order on every page: page-head → banners → KPI row → failing/actionable panels → reference panels → empty panels (collapsed).
- Vertical rhythm: 16px between panels, 12px inside. Table row height ~36px (default), ~28px (`.compact`, used for matrices and >20-row tables).
- Prose (headlines, descriptions, finding text) max 72ch.

**Mono** — only for: commit SHAs (7 chars), work-item/run/build IDs, file paths, code/YAML, raw error output (`pre.box`). **Not** for versions, branch names, repo names, pipeline names, numbers.

**Colour semantics**
- `bad` (red) = actionable failure a human must fix now: main red, failed prod deploy, blocked gate, critical finding. Never for "zero", decorative, or historical data.
- `warn` = degraded/drift/stale/non-default branch deployed. `ok` (green) = only for a state that was in question (gate passed, deploy succeeded) — never colour plain counts green.
- `info` = in progress/neutral notice. `accent` = interactive + "needs you".
- Colour is never the only signal: pair with text or icon (`⚠`, `✕`, `✓`).
- Tier colours only on tier pills. Env colours only on env dots/pills and matrix column headers.

**Numbers**
- Always `tabular-nums`, right-aligned in tables.
- Always a unit: `42 min`, `3.1 d`, `87 %`, `12 deploys`. Durations: `<90s → "Ns"`, `<90m → "N min"`, `<48h → "N h"`, else `N d` (1 decimal under 10).
- Always a window label on aggregates: `30d`, `7d`, `24h`, `since Thu 20:53`. Default window 30d unless stated.
- Deltas: `▲ 12 %` / `▼ 4 %` with tone only when direction is meaningfully bad.
- Thousands: `12,400`; ≥100k → `124k`.

**Truncation**
- Repo/component names: `.trunc` at 32ch with `title`. Branch refs: pill max 14ch, keep prefix (`feature/1234…`). Pipeline/job messages: `.clamp-2` + "more" toggle. Never let a table cause page-level horizontal scroll — wrap in `.table-wrap`.

**Same / no data**
- Same as cell to the left (promotion path): `= same` in `--subtle`, plus ago. 
- No data / never deployed: `—` in `--subtle`, no tooltip needed.
- Not applicable / not configured: `n/a` in `--subtle` with info-tip explaining why.
- Zero is a value: render `0`, not `—`.
- Loading: `.empty` "Loading…" for <1s; skeleton not required.

**Ordering** — failing first, then needs-you, then warn, then by recency. Repo tables default sort: worst status → oldest failure → name. Environments left→right in promotion order (team → staging → sim → prod).

**People** — team-visible: no per-person rankings, leaderboards, or "top N contributors". Per-person data appears only as "you" (your approvals) or as an author on a specific item. `.person` elements hide in presentation mode.

**Copy & tone** — sentence case everywhere ("Needs attention", not "Needs Attention"). Plain, factual, present tense: "main red for 27d", not "Uh oh!". No emoji in UI text (use text glyphs ⚠ ✓ ✕ ↗ only). Links out end with `↗` ("Open in ADO ↗"). Buttons are verbs ("Sync now", "Dismiss"). Empty states say what is absent: "Nothing running", "No findings in 30d".

**Glossary** (use exactly these terms)
| Term | Meaning |
|---|---|
| Repo | Azure DevOps git repository |
| Component | Deployable unit within a repo (service, DB, AKS app, SF app) |
| Environment | Deploy target (staging-green, sim-eu, prod-green…) |
| Env tier | Team · Staging · Sim · Prod |
| Main red | Latest CI run on default branch failed |
| Drift | Sim and prod run different versions of a component |
| Behind (+N) | Commits merged since the deployed build was queued |
| Gate | Approval/check blocking a stage |
| Finding | Rule violation from the maturity audit |
| Tier | Gold · Silver · Bronze · None maturity rating |
| Lead time | PR merge → prod deploy, median, 30d |
| Deploy | Successful stage completion to an environment |
| Stale | Not deployed / not synced beyond threshold |
Avoid: "release" (ambiguous), "build" for deploys, "health score".

---

## 4. Migration map

| Old | New |
|---|---|
| `--bg / --panel / --panel-2` | `--surface-0 / --surface-1 / --surface-2` |
| `--ok / --ok-soft` (etc.) | `--ok-fg / --ok-bg` (warn, bad, info likewise) |
| `--neutral-soft` | `--neutral-bg` |
| `--gold/--silver/--bronze` | `--tier-*-fg` + `--tier-*-bg` |
| `--radius` | `--r-lg` (panels) / `--r-md` (controls) |
| `--font / --mono` | `--font-ui / --font-mono` |
| `<div class="row"><h1 style=…>` + `as of` | `.page-head` with headline |
| `.panel > header`, `.meta` | `.panel__head`, `.panel__meta` |
| panel containing only `.empty` | `.panel.is-empty[data-empty]` |
| `.kpis` / `.kpi .label/.value/.sub` | `.kpi-row` / `.kpi__label/__value/__sub` (+ `.kpi__window`) |
| `.kpi .value.bad/.good/.warnc` | `.kpi--bad` / (none) / `.kpi--warn` |
| `.badge`, `.badge.ok/warn/bad/info` | `.pill`, `.pill--ok/warn/bad/info` |
| `.badge` used as counter | `.pill.pill--count` |
| `.tier.Gold/Silver/Bronze/None`, `.tier .prov` | `.pill--gold/silver/bronze/none`, `.pill__sub` |
| `.cellv .br`, `.br.feat` | `.pill.pill--ref`, `.pill--ref.is-nondefault` (omit for main/master) |
| `.cellv`, `.ver`, `.age`, `.same`, `.drift` | `.ver-cell`, `.ver` (UI font), `.ver-cell__meta`, `.is-same`, `.is-drift` |
| `.nav .count` | `.pill.pill--count.pill--bad` |
| `.brand .mode` | `.pill.pill--warn` |
| `.chip.db::before` emoji | `.chip` + `<span class="chip__kind">DB</span>` |
| `.good` / `.bad` / `.warnc` (text) | `.tone-ok` / `.tone-bad` / `.tone-warn` |
| `.muted`, `.small` | `.tone-muted`, `.text-sm` |
| `.queue`, `.qitem.kN`, `.icon` | `.att-list`, `.att.att--you/bad/warn/info`, (icon removed) |
| `.banner.bad/warn/info` | `.banner--bad/--warn` / `.banner` |
| `.dot.ok` etc. | `.dot--ok` etc. |
| `button`, `button.primary`, `button.danger` | `.btn.btn--secondary`, `.btn--primary`, `.btn--danger` |
| bare `input/select/textarea` | `.input`, `.select`, `.textarea` |
| `.tabs a.active` | `.tabs a.is-active` |
| `.info` | `.info-tip` + `.info-tip__body` |
| `table.findings td.ev div`, `.jobs .jobmsg div:hover` | `.trunc`, `.clamp-2` + `.clamp-toggle` |
| `table.t code { nowrap }` | `.nowrap` on the cell; mono only for IDs/SHAs |
| `style="margin…"` | `.mt-2` / `.mt-4` or component gap |
| `🎉` and emoji in copy | plain text |

Migration order: tokens (keep old names as aliases for one release) → pill/page-head/panel → tables/ver-cell → remove aliases and old classes.
