// Core UI behaviours shared by every page: toasts, popovers, drawers, theme, the confirm dialog,
// segmented controls, form submit states, amount inputs and small conveniences.
// Nothing here animates on its own: every visual change answers something the person did.

const reduceMotion = () => matchMedia("(prefers-reduced-motion: reduce)").matches;
const money = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD" });
export const formatMoney = (n) => money.format(n);

// ---------- Toasts ----------
const ICONS = { success: "check", error: "alert", info: "info" };
export function toast(type, title, body = "", timeout = 4800) {
  let host = document.querySelector(".toasts");
  if (!host) {
    host = document.createElement("div");
    host.className = "toasts";
    host.setAttribute("aria-live", "polite");
    document.body.append(host);
  }
  const el = document.createElement("div");
  el.className = `toast ${type}`;
  el.setAttribute("role", type === "error" ? "alert" : "status");
  el.innerHTML = `<svg class="icon" aria-hidden="true"><use href="#i-${ICONS[type] ?? "info"}"/></svg>
    <div><div class="toast-title"></div><div class="toast-body"></div></div>
    <button type="button" aria-label="Dismiss"><svg class="icon icon-sm"><use href="#i-x"/></svg></button>`;
  el.querySelector(".toast-title").textContent = title;
  el.querySelector(".toast-body").textContent = body;
  const remove = () => { el.classList.add("leaving"); el.addEventListener("animationend", () => el.remove(), { once: true }); };
  el.querySelector("button").addEventListener("click", remove);
  host.append(el);
  if (timeout) setTimeout(remove, timeout);
}

function flashFromServer() {
  document.querySelectorAll("template[data-flash]").forEach((t) => {
    toast(t.dataset.flash, t.dataset.title || "", t.innerHTML.trim());
  });
}

// ---------- Popovers (avatar menu, notifications) ----------
function initPopovers() {
  document.addEventListener("click", (e) => {
    const trigger = e.target.closest("[data-popover]");
    const openOnes = document.querySelectorAll(".popover.open");
    if (trigger) {
      const pop = document.getElementById(trigger.dataset.popover);
      const willOpen = !pop.classList.contains("open");
      openOnes.forEach((p) => close(p));
      if (willOpen) {
        pop.classList.add("open");
        trigger.setAttribute("aria-expanded", "true");
        pop.dispatchEvent(new CustomEvent("popover:open"));
      }
      return;
    }
    openOnes.forEach((p) => { if (!p.contains(e.target)) close(p); });
  });
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") document.querySelectorAll(".popover.open").forEach(close);
  });
  function close(p) {
    p.classList.remove("open");
    document.querySelectorAll(`[data-popover="${p.id}"]`).forEach((t) => t.setAttribute("aria-expanded", "false"));
  }
}

// ---------- Drawers & bottom sheets ----------
let lastFocus = null;
export function openDrawer(id) {
  const drawer = document.getElementById(id);
  if (!drawer) return;
  lastFocus = document.activeElement;
  let scrim = document.querySelector(".scrim");
  if (!scrim) {
    scrim = document.createElement("div");
    scrim.className = "scrim";
    scrim.addEventListener("click", closeDrawers);
    document.body.append(scrim);
  }
  requestAnimationFrame(() => { scrim.classList.add("open"); drawer.classList.add("open"); });
  drawer.setAttribute("aria-hidden", "false");
  document.querySelectorAll(`[data-drawer-open="${id}"]`).forEach((t) => t.setAttribute("aria-expanded", "true"));
  document.body.style.overflow = "hidden";
  setTimeout(() => drawer.querySelector("[autofocus], button, a, input")?.focus({ preventScroll: true }), 50);
  initSheetDrag(drawer);
}
export function closeDrawers() {
  document.querySelectorAll(".drawer.open").forEach((d) => {
    d.classList.remove("open");
    d.setAttribute("aria-hidden", "true");
    d.style.transform = "";
    document.querySelectorAll(`[data-drawer-open="${d.id}"]`).forEach((t) => t.setAttribute("aria-expanded", "false"));
  });
  document.querySelector(".scrim")?.classList.remove("open");
  document.body.style.overflow = "";
  lastFocus?.focus?.({ preventScroll: true });
}
function initDrawers() {
  document.addEventListener("click", (e) => {
    const opener = e.target.closest("[data-drawer-open]");
    if (opener) { e.preventDefault(); openDrawer(opener.dataset.drawerOpen); return; }
    if (e.target.closest("[data-drawer-close]")) closeDrawers();
  });
  document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeDrawers(); });
}
// Swipe a bottom sheet down to dismiss it (phones).
function initSheetDrag(drawer) {
  if (drawer.dataset.dragReady || !matchMedia("(max-width: 760px)").matches) return;
  drawer.dataset.dragReady = "1";
  const handle = drawer.querySelector(".sheet-grip")?.parentElement ?? drawer;
  let startY = null, dy = 0;
  handle.addEventListener("touchstart", (e) => {
    if (drawer.querySelector(".drawer-body")?.scrollTop > 0) return;
    startY = e.touches[0].clientY; dy = 0; drawer.style.transition = "none";
  }, { passive: true });
  handle.addEventListener("touchmove", (e) => {
    if (startY === null) return;
    dy = Math.max(0, e.touches[0].clientY - startY);
    drawer.style.transform = `translateY(${dy}px)`;
  }, { passive: true });
  handle.addEventListener("touchend", () => {
    if (startY === null) return;
    drawer.style.transition = "";
    if (dy > 110) closeDrawers(); else drawer.style.transform = "";
    startY = null;
  });
}

// ---------- Theme ----------
export function currentTheme() { return document.documentElement.dataset.theme; }
// The toggle's label and tooltip always say what pressing it will do.
function syncThemeButtons() {
  const label = currentTheme() === "dark" ? "Switch to light mode" : "Switch to dark mode";
  document.querySelectorAll("[data-theme-toggle]").forEach((b) => {
    b.setAttribute("aria-label", label);
    if (b.hasAttribute("data-tip")) b.setAttribute("data-tip", label);
  });
}
function setThemeColor(theme) {
  document.querySelector('meta[name="theme-color"]')?.setAttribute("content", theme === "dark" ? "#16121a" : "#f5f6f8");
}
function initTheme() {
  syncThemeButtons();
  document.addEventListener("click", (e) => {
    const btn = e.target.closest("[data-theme-toggle]");
    if (!btn) return;
    const next = currentTheme() === "dark" ? "light" : "dark";
    document.documentElement.dataset.theme = next;
    localStorage.setItem("sb-theme", next);
    setThemeColor(next);
    syncThemeButtons();
    window.dispatchEvent(new CustomEvent("themechange", { detail: next }));
    const csrf = document.querySelector('meta[name="csrf-token"]')?.content;
    if (csrf && document.body.dataset.auth === "1") {
      fetch("/api/v1/profile/theme", {
        method: "POST", headers: { "Content-Type": "application/json", RequestVerificationToken: csrf },
        body: JSON.stringify({ theme: next })
      }).catch(() => {});
    }
  });
  addEventListener("themechange", () => { syncThemeButtons(); setThemeColor(currentTheme()); });
  // Follow the OS when the user hasn't picked a theme explicitly.
  matchMedia("(prefers-color-scheme: dark)").addEventListener("change", (m) => {
    if (localStorage.getItem("sb-theme") || document.documentElement.dataset.pref !== "system") return;
    document.documentElement.dataset.theme = m.matches ? "dark" : "light";
    window.dispatchEvent(new CustomEvent("themechange"));
  });
}

// ---------- Numbers ----------
// Kept for callers that update a figure in place. Numbers never count up: the new value is set at once.
export function countUp(el, to, { format = formatMoney } = {}) {
  el.textContent = format(to);
}

// ---------- Segmented controls ----------
export function syncSegmented(seg) {
  const active = seg.querySelector('[aria-selected="true"], [aria-current="page"]');
  let thumb = seg.querySelector(".seg-thumb");
  if (!thumb) { thumb = document.createElement("span"); thumb.className = "seg-thumb"; seg.prepend(thumb); }
  if (!active) { thumb.style.width = "0"; return; }
  thumb.style.width = `${active.offsetWidth}px`;
  thumb.style.transform = `translateX(${active.offsetLeft - 3}px)`;
  thumb.style.left = "3px";
}
function initSegmented() {
  document.querySelectorAll(".segmented").forEach((seg) => {
    syncSegmented(seg);
    seg.addEventListener("click", (e) => {
      const b = e.target.closest("button[role=tab]");
      if (!b) return;
      seg.querySelectorAll("button[role=tab]").forEach((x) => x.setAttribute("aria-selected", String(x === b)));
      syncSegmented(seg);
      seg.dispatchEvent(new CustomEvent("segment:change", { detail: b.dataset.value }));
    });
  });
  addEventListener("resize", () => document.querySelectorAll(".segmented").forEach(syncSegmented));
  document.fonts?.ready.then(() => document.querySelectorAll(".segmented").forEach(syncSegmented));
}

// ---------- Confirm dialog ----------
// Buttons and links with data-confirm="..." ask first in a styled <dialog>.
// Optional: data-confirm-title, data-confirm-ok (the confirm button's label).
// The dialog doesn't block like window.confirm(), so the original click is cancelled and,
// when "Confirm" is pressed, the same action runs directly from that click.
function initConfirm() {
  const dialog = document.getElementById("confirm-dialog");
  let pending = null; // the button or link waiting for an answer

  document.addEventListener("click", (e) => {
    const trigger = e.target.closest("[data-confirm]");
    if (!trigger || trigger.dataset.confirmed === "1") return;
    e.preventDefault();
    e.stopImmediatePropagation();
    if (!dialog?.showModal) {
      if (window.confirm(trigger.dataset.confirm)) proceed(trigger);
      return;
    }
    pending = trigger;
    dialog.querySelector("#confirm-title").textContent = trigger.dataset.confirmTitle || "Are you sure?";
    dialog.querySelector("#confirm-text").textContent = trigger.dataset.confirm;
    dialog.querySelector("[data-dialog-ok]").textContent = trigger.dataset.confirmOk || "Confirm";
    dialog.showModal();
  }, true);

  if (dialog) {
    dialog.querySelector("[data-dialog-ok]").addEventListener("click", (e) => {
      e.preventDefault();
      const trigger = pending;
      pending = null;
      dialog.close("ok");
      if (trigger) proceed(trigger);
    });
    // Cancel, Escape or the backdrop: nothing happens and focus goes back to the button.
    dialog.addEventListener("close", () => {
      if (!pending) return;
      const trigger = pending;
      pending = null;
      trigger.focus?.({ preventScroll: true });
    });
  }

  function proceed(trigger) {
    if (trigger.tagName === "A") { location.href = trigger.href; return; }
    const form = trigger.form ?? trigger.closest("form");
    if (!form) return;
    delete form.dataset.submitted;              // let the double-submit guard see a fresh submit
    trigger.dataset.confirmed = "1";            // so the repeated click isn't asked again
    if (trigger.type === "submit") form.requestSubmit(trigger); else form.requestSubmit();
    delete trigger.dataset.confirmed;
  }
}

function initForms() {
  document.addEventListener("submit", (e) => {
    const form = e.target;
    if (form.method === "dialog" || form.dataset.noLoading !== undefined || e.defaultPrevented) return;
    const btn = e.submitter ?? form.querySelector("[type=submit]");
    if (!btn || !btn.classList.contains("btn")) return;
    if (form.dataset.submitted) { e.preventDefault(); return; } // block double submits
    form.dataset.submitted = "1";
    if (!btn.querySelector(".spinner")) btn.insertAdjacentHTML("beforeend", '<span class="spinner" aria-hidden="true"></span>');
    btn.classList.add("is-loading");
    btn.setAttribute("aria-busy", "true");
  });
  // Pages restored from the back/forward cache must be usable again.
  addEventListener("pageshow", (e) => {
    if (!e.persisted) return;
    document.querySelectorAll("form[data-submitted]").forEach((f) => delete f.dataset.submitted);
    document.querySelectorAll(".btn.is-loading").forEach((b) => b.classList.remove("is-loading"));
  });
}

// ---------- Amount inputs ----------
function initAmountInputs() {
  document.querySelectorAll(".amount-input").forEach((input) => {
    const size = () => { input.style.width = `${Math.max(2, (input.value || input.placeholder).length + 0.5)}ch`; };
    input.addEventListener("input", () => {
      // Keep at most two decimals.
      const m = input.value.match(/^(\d*)(\.\d{0,2})?/);
      if (m && m[0] !== input.value) input.value = m[0];
      size();
    });
    size();
  });
  document.addEventListener("click", (e) => {
    const wrap = e.target.closest("[data-focus-input]");
    if (wrap && e.target.tagName !== "INPUT") wrap.querySelector("input")?.focus();
    const b = e.target.closest("[data-amount]");
    if (!b) return;
    const input = document.querySelector(b.dataset.target || ".amount-input");
    if (!input) return;
    input.value = b.dataset.amount;
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.focus();
  });
}

// ---------- Small conveniences ----------
function initMisc() {
  const topbar = document.querySelector(".topbar");
  if (topbar) {
    const onScroll = () => topbar.classList.toggle("scrolled", scrollY > 4);
    addEventListener("scroll", onScroll, { passive: true });
    onScroll();
  }
  document.addEventListener("click", async (e) => {
    const rev = e.target.closest("[data-reveal]");
    if (rev) {
      const target = document.getElementById(rev.dataset.reveal);
      const shown = target.dataset.shown === "1";
      target.textContent = shown ? target.dataset.masked : target.dataset.full;
      target.dataset.shown = shown ? "0" : "1";
      rev.setAttribute("aria-pressed", String(!shown));
      rev.querySelector("use")?.setAttribute("href", shown ? "#i-eye" : "#i-eye-off");
      const label = rev.querySelector("[data-reveal-label]");
      if (label) label.textContent = shown ? "Show" : "Hide";
      rev.setAttribute("aria-label", shown ? "Show full account number" : "Hide account number");
    }
    const copy = e.target.closest("[data-copy]");
    if (copy) {
      try { await navigator.clipboard.writeText(copy.dataset.copy); toast("success", "Copied", copy.dataset.copyLabel || ""); }
      catch { toast("error", "Couldn't copy", "Your browser blocked clipboard access."); }
    }
  });
  // Auto-submit selects inside [data-autosubmit] forms.
  document.addEventListener("change", (e) => {
    const form = e.target.closest("form[data-autosubmit]");
    if (form && e.target.matches("select, input[type=date], input[type=radio]")) form.requestSubmit();
  });
}

export function init() {
  initTheme();
  initPopovers();
  initDrawers();
  initConfirm();
  initSegmented();
  initForms();
  initAmountInputs();
  initMisc();
  flashFromServer();
}
