// Dashboard: the three charts (fed by /api/v1/insights), the balance count-up, and live balance refresh.
import { api } from "./api.js";
import { countUp } from "./ui.js";
import { lineChart, donutChart, barChart, categoryColor, cssVar, fmt } from "./charts.js";

const charts = {};
let balanceDays = 30;
const cache = {};

const dateLabel = new Intl.DateTimeFormat("en-US", { month: "short", day: "numeric" });
const longDate = new Intl.DateTimeFormat("en-US", { weekday: "short", month: "short", day: "numeric" });
const parseDay = (s) => { const [y, m, d] = s.split("-").map(Number); return new Date(y, m - 1, d); };

function ready(canvas) { canvas.closest(".chart-box")?.classList.add("ready"); }
function swap(key, make) { charts[key]?.destroy(); charts[key] = make(); }

async function drawBalance(force = false) {
  const canvas = document.getElementById("balance-chart");
  if (!canvas) return;
  const key = `bal-${balanceDays}`;
  if (force || !cache[key]) cache[key] = (await api(`insights/balance-history?days=${balanceDays}`)).items;
  const points = cache[key];
  const days = points.map((p) => parseDay(p.date));
  swap("balance", () => lineChart(canvas, days.map((d) => dateLabel.format(d)), points.map((p) => p.balance),
    { label: "Total balance", tooltipTitles: days.map((d) => longDate.format(d)) }));
  ready(canvas);
}

async function drawSpending(force = false) {
  const canvas = document.getElementById("spend-chart");
  if (!canvas) return;
  if (force || !cache.spend) cache.spend = (await api("insights/spending")).items;
  const all = cache.spend;
  // At most five named categories; the rest fold into "Other" so no hue is ever generated.
  const top = all.slice(0, 5);
  const rest = all.slice(5).reduce((s, c) => s + c.amount, 0);
  const rows = [...top.map((c) => ({ name: c.category, amount: c.amount, color: categoryColor(c.category), cat: c.category }))];
  if (rest > 0) rows.push({ name: "Other", amount: rest, color: categoryColor("Other"), cat: null });
  const total = all.reduce((s, c) => s + c.amount, 0);

  document.querySelector("[data-spend-total]").textContent = fmt.usd(total);
  const legend = document.querySelector("[data-spend-legend]");
  legend.innerHTML = "";
  if (!rows.length) {
    legend.innerHTML = '<li class="muted" style="display:block">No spending yet this month.</li>';
  }
  for (const r of rows) {
    const li = document.createElement("li");
    li.style.setProperty("--c", r.color);
    const name = r.cat ? `<a href="/Transactions?category=${r.cat}&flow=out"></a>` : "<span></span>";
    li.innerHTML = `<span class="sw"></span>${name}<span class="v"></span>`;
    li.children[1].textContent = r.name;
    li.querySelector(".v").textContent = fmt.usd(r.amount);
    legend.append(li);
  }
  swap("spend", () => donutChart(canvas, rows.map((r) => r.name), rows.map((r) => r.amount), rows.map((r) => r.color)));
  ready(canvas);
}

async function drawCashflow(force = false) {
  const canvas = document.getElementById("cashflow-chart");
  if (!canvas) return;
  if (force || !cache.flow) cache.flow = (await api("insights/cashflow?months=6")).items;
  const m = cache.flow;
  swap("flow", () => barChart(canvas, m.map((x) => x.label), [
    { label: "In", values: m.map((x) => x.moneyIn), color: cssVar("--positive") },
    { label: "Out", values: m.map((x) => x.moneyOut), color: categoryColor("Transfer") }
  ]));
  ready(canvas);
}

function drawAll(force = false) {
  return Promise.allSettled([drawBalance(force), drawSpending(force), drawCashflow(force)]);
}

// Range toggle (30d / 90d / 6m).
document.querySelector('[data-range-for="balance-chart"]')?.addEventListener("segment:change", (e) => {
  balanceDays = Number(e.detail);
  drawBalance();
});

// Re-color charts when the theme flips (after the view transition has applied the new variables).
addEventListener("themechange", () => setTimeout(() => drawAll(), 30));

// The total on the banknote counts up on load.
const dollars = document.querySelector("[data-countup-dollars]");
if (dollars) countUp(dollars, Number(dollars.dataset.countupDollars), { format: (n) => Math.round(n).toLocaleString("en-US"), duration: 1300 });

// Live refresh: when a new notification arrives (e.g. someone paid you), update balances in place.
document.addEventListener("bank:refresh", async () => {
  try {
    const { items } = await api("accounts");
    let total = 0;
    for (const a of items) {
      total += a.balance;
      const el = document.querySelector(`[data-account-id="${a.id}"] [data-balance]`);
      if (el && Number(el.dataset.balance) !== a.balance) {
        countUp(el, a.balance, { from: Number(el.dataset.balance), duration: 900 });
        el.dataset.balance = a.balance;
        el.closest(".acct-card")?.animate([{ boxShadow: `0 0 0 3px ${cssVar("--primary")}` }, { boxShadow: "0 0 0 0 transparent" }], { duration: 1200 });
      }
    }
    const note = document.querySelector("[data-live-total]");
    if (note && Number(note.dataset.liveTotal) !== total) {
      countUp(note.querySelector("[data-countup-dollars]"), Math.trunc(total), { from: Math.trunc(Number(note.dataset.liveTotal)), format: (n) => Math.round(n).toLocaleString("en-US") });
      note.querySelector("sup").textContent = "." + String(Math.round((total % 1) * 100)).padStart(2, "0");
      note.dataset.liveTotal = total;
    }
    drawAll(true);
  } catch { /* ignore */ }
});

// Wait for fonts so Chart.js measures labels with the right typeface.
(document.fonts?.ready ?? Promise.resolve()).then(() => drawAll());
