// Transaction lists: live filtering without page reloads, AJAX pagination,
// and the detail drawer that opens when a row is clicked.
import { openDrawer } from "./ui.js";

const host = document.querySelector("[data-results-host]");
const form = document.querySelector("[data-filter-form]");
let controller = null;

async function load(url, { push = true } = {}) {
  if (!host) { location.href = url; return; }
  controller?.abort();
  controller = new AbortController();
  host.setAttribute("aria-busy", "true");
  host.classList.add("is-loading");
  try {
    const res = await fetch(url, { headers: { "X-Partial": "1" }, signal: controller.signal });
    if (!res.ok) throw new Error(res.statusText);
    const html = await res.text();
    const swap = () => { host.innerHTML = html; };
    if (document.startViewTransition && !matchMedia("(prefers-reduced-motion: reduce)").matches) {
      host.style.viewTransitionName = "results";
      await document.startViewTransition(swap).finished.catch(() => {});
      host.style.viewTransitionName = "";
    } else swap();
    if (push) history.replaceState(null, "", url);
  } catch (e) {
    if (e.name !== "AbortError") location.href = url;
  } finally {
    host.setAttribute("aria-busy", "false");
    host.classList.remove("is-loading");
  }
}

function formUrl() {
  const params = new URLSearchParams();
  for (const [k, v] of new FormData(form)) {
    if (v === "" || (k === "sort" && v === "date_desc") || (k === "pageSize" && v === "10")) continue;
    params.append(k, v);
  }
  const qs = params.toString();
  return location.pathname + (qs ? `?${qs}` : "");
}

if (form) {
  let t;
  form.addEventListener("submit", (e) => { e.preventDefault(); load(formUrl()); });
  form.addEventListener("input", (e) => {
    if (e.target.name === "q") { clearTimeout(t); t = setTimeout(() => load(formUrl()), 280); }
  });
  form.addEventListener("change", (e) => {
    if (e.target.name === "q") return;
    if (e.target.type === "radio") {
      // Reflect the radio state on the styled chips / segmented control.
      form.querySelectorAll(`input[name="${e.target.name}"]`).forEach((r) => {
        const label = r.closest("label");
        label.classList.toggle("on", r.checked);
        label.setAttribute("aria-checked", String(r.checked));
        label.setAttribute("aria-selected", String(r.checked));
      });
      const seg = e.target.closest(".segmented");
      if (seg) import("./ui.js").then((m) => m.syncSegmented(seg));
    }
    load(formUrl());
  });
  const toggle = form.querySelector("[data-filters-toggle]");
  toggle?.addEventListener("click", () => {
    const more = document.getElementById(toggle.getAttribute("aria-controls"));
    more.hidden = !more.hidden;
    toggle.setAttribute("aria-expanded", String(!more.hidden));
  });
  // Segmented control built from radio labels.
  form.querySelectorAll(".segmented .seg-radio").forEach((l) => l.setAttribute("tabindex", "-1"));
}

// Pagination and "clear filters" links load in place.
document.addEventListener("click", (e) => {
  const link = e.target.closest("[data-page-link], [data-clear-filters]");
  if (!link || !host || e.metaKey || e.ctrlKey) return;
  e.preventDefault();
  if (link.hasAttribute("data-clear-filters") && form) {
    form.reset();
    form.querySelectorAll("input[type=search], input[type=date]").forEach((i) => (i.value = ""));
    form.querySelectorAll("select").forEach((s) => (s.selectedIndex = 0));
    form.querySelectorAll('input[type=radio][value=""]').forEach((r) => { r.checked = true; r.dispatchEvent(new Event("change", { bubbles: true })); });
    return;
  }
  load(link.href).then(() => host.scrollIntoView({ behavior: "smooth", block: "start" }));
});

// Detail drawer.
document.addEventListener("click", async (e) => {
  const row = e.target.closest("[data-tx]");
  if (!row) return;
  const drawer = document.getElementById("tx-drawer");
  if (!drawer) return;
  const content = drawer.querySelector("[data-drawer-content]");
  content.innerHTML = `<div class="stack"><div class="skeleton" style="height:120px"></div><div class="skeleton" style="height:220px"></div></div>`;
  openDrawer("tx-drawer");
  try {
    const res = await fetch(`${drawer.dataset.detailUrl}${row.dataset.tx}`, { headers: { "X-Partial": "1" } });
    content.innerHTML = res.ok ? await res.text() : '<p class="muted">Couldn\'t load this transaction.</p>';
  } catch {
    content.innerHTML = '<p class="muted">Couldn\'t load this transaction.</p>';
  }
});
