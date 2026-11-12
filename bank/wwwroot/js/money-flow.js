// Money flows: details → review → submit. Without JS the form posts straight from step 1,
// and the server still validates everything; this script only adds the review step and live search.
import { api } from "./api.js";
import { formatMoney, toast } from "./ui.js";

const form = document.querySelector("[data-money-form]");
const kind = document.querySelector("[data-flow]")?.dataset.flow;
const amountInput = form.querySelector(".amount-input");
const steps = [...form.querySelectorAll("[data-step]")];
const dots = [...document.querySelectorAll("[data-step-dot]")];
let step = 1;

function show(n) {
  step = n;
  steps.forEach((s) => {
    const on = Number(s.dataset.step) === n;
    s.hidden = !on;
  });
  dots.forEach((d) => {
    const i = Number(d.dataset.stepDot);
    d.classList.toggle("is-current", i === n);
    d.classList.toggle("is-done", i < n);
  });
  window.scrollTo({ top: 0 });
}

const who = form.querySelector("[data-recipient-search] input");
const scheduled = () => form.querySelector('[name="When"]:checked')?.value === "later";
const amount = () => parseFloat(amountInput.value || "0");
const picked = (name) => form.querySelector(`input[name="${name}"]:checked`);

function fieldError(name, message) {
  const el = form.querySelector(`[data-valmsg-for="${name}"]`);
  if (el) { el.textContent = message; el.classList.remove("field-valid"); }
  return !message;
}

function validate() {
  let ok = true;
  const a = amount();
  if (!(a > 0)) ok = fieldError("Amount", "Enter an amount.") && ok;
  else fieldError("Amount", "");
  const from = picked("FromAccountId");
  if (form.querySelector('input[name="FromAccountId"]') && !from) { toast("error", "Choose an account to pay from"); ok = false; }
  if (from && a > parseFloat(from.dataset.balance) && kind !== "deposit" && !scheduled()) {
    fieldError("Amount", `That's more than the ${formatMoney(parseFloat(from.dataset.balance))} in ${from.dataset.name}.`);
    ok = false;
  }
  const to = picked("ToAccountId");
  if (form.querySelector('input[name="ToAccountId"]') && !to) { toast("error", "Choose an account to pay into"); ok = false; }
  if (from && to && from.value === to.value) { toast("error", "Pick two different accounts"); ok = false; }
  if (form.querySelector('input[name="PayeeId"]') && !picked("PayeeId")) { toast("error", "Choose a payee"); ok = false; }
  if (who && !who.value.trim()) { fieldError(who.name, kind === "request" ? "Choose who to ask." : "Choose who to pay."); ok = false; }
  const when = form.querySelector('[name="RunDate"]');
  if (scheduled() && when && !when.value) { toast("error", "Choose a date for the payment"); ok = false; }
  if (!ok) form.querySelector(".field-error:not(:empty)")?.scrollIntoView({ block: "center" });
  return ok;
}

function fillReview() {
  const a = amount();
  const from = picked("FromAccountId");
  const to = picked("ToAccountId");
  const set = (sel, text) => { const el = form.querySelector(sel); if (el) el.textContent = text; };
  set("[data-review-amount]", formatMoney(a));
  set("[data-cta-amount]", formatMoney(a));
  set("[data-review-from]", from?.dataset.name ?? "");
  set("[data-review-to]", to?.dataset.name ?? "");
  set("[data-review-memo]", form.querySelector("#Memo, #Note")?.value.trim() || "None");
  set("[data-review-recipient]", form.dataset.recipientLabel || who?.value || "");
  set("[data-review-payee]", picked("PayeeId")?.dataset.name ?? "");
  // "Balance after" follows the account money leaves, or the one it lands in for a deposit.
  const acct = kind === "deposit" ? to : from;
  if (acct) {
    const after = parseFloat(acct.dataset.balance) + (kind === "deposit" ? a : -a);
    set("[data-review-after-label]", `${acct.dataset.name} after`);
    set("[data-review-after]", formatMoney(after));
  }
  // Scheduled payments post later, so the review says when instead of showing a new balance.
  const later = scheduled();
  form.querySelectorAll("[data-review-now]").forEach((el) => { el.hidden = later; });
  form.querySelectorAll("[data-review-later]").forEach((el) => { el.hidden = !later; });
  if (later) {
    const date = new Date(`${form.querySelector('[name="RunDate"]').value}T00:00`);
    const repeat = form.querySelector('[name="Frequency"]');
    const every = repeat && repeat.value !== "Once" ? `, then ${repeat.selectedOptions[0].textContent.toLowerCase()}` : "";
    set("[data-review-when]", date.toLocaleDateString("en-US", { month: "long", day: "numeric", year: "numeric" }) + every);
  }
}

form.addEventListener("submit", (e) => {
  if (step === 1) {
    e.preventDefault();
    if (!validate()) return;
    fillReview();
    show(2);
    form.querySelector("[data-confirm-submit]").focus({ preventScroll: true });
    return;
  }
  // Final submit: lock the form and show progress.
  if (form.dataset.submitted) { e.preventDefault(); return; }
  form.dataset.submitted = "1";
  const btn = form.querySelector("[data-confirm-submit]");
  btn.insertAdjacentHTML("beforeend", '<span class="spinner" aria-hidden="true"></span>');
  btn.classList.add("is-loading");
  dots[2]?.classList.add("is-current");
});
form.querySelector("[data-back]").addEventListener("click", () => show(1));
addEventListener("pageshow", (e) => { if (e.persisted) { delete form.dataset.submitted; form.querySelectorAll(".is-loading").forEach((b) => b.classList.remove("is-loading")); show(1); } });

if (!form.querySelector(".form-errors li") && (!who || form.querySelector("[data-recipient-search][hidden]"))) amountInput.focus();

// "Pay now" or "On a date": the date and repeat fields only show for the second.
const schedule = form.querySelector("[data-schedule-fields]");
if (schedule) {
  const sync = () => { schedule.hidden = !scheduled(); };
  form.querySelectorAll('[name="When"]').forEach((r) => r.addEventListener("change", sync));
  sync();
}

// ---------- Recipient search (send only) ----------
const search = form.querySelector("[data-recipient-search]");
if (search) {
  const input = search.querySelector("input");
  const list = search.querySelector(".recipient-results");
  const pickedBox = form.querySelector("[data-recipient-picked]");
  let timer, active = -1, results = [];

  const close = () => { list.classList.remove("open"); input.setAttribute("aria-expanded", "false"); active = -1; };
  const choose = (u) => {
    input.value = `@${u.handle}`;
    form.dataset.recipientLabel = `${u.name} (@${u.handle})`;
    pickedBox.innerHTML = `<span class="avatar" style="--c:${u.avatarColor}">${u.initials}</span>
      <span><strong></strong><small class="muted"></small></span>
      <button type="button" class="btn btn-ghost btn-sm" data-recipient-clear>Change</button>`;
    pickedBox.querySelector("strong").textContent = u.name;
    pickedBox.querySelector("small").textContent = `@${u.handle}`;
    pickedBox.hidden = false;
    search.hidden = true;
    fieldError(input.name, "");
    close();
    amountInput.focus();
  };
  const render = () => {
    list.innerHTML = "";
    if (!results.length) {
      list.innerHTML = `<li class="recipient-empty muted">No one matches “${input.value.replace(/[<>&]/g, "")}”.</li>`;
    }
    results.forEach((u, i) => {
      const li = document.createElement("li");
      li.role = "option";
      li.id = `rec-${i}`;
      li.className = "menu-item" + (i === active ? " active" : "");
      li.innerHTML = `<span class="avatar avatar-sm" style="--c:${u.avatarColor}">${u.initials}</span><span><strong></strong><small class="muted"></small></span>`;
      li.querySelector("strong").textContent = u.name;
      li.querySelector("small").textContent = `@${u.handle}`;
      li.addEventListener("mousedown", (e) => { e.preventDefault(); choose(u); });
      list.append(li);
    });
    list.classList.add("open");
    input.setAttribute("aria-expanded", "true");
  };

  input.addEventListener("input", () => {
    clearTimeout(timer);
    const q = input.value.trim();
    form.dataset.recipientLabel = "";
    if (q.length < 2) { close(); return; }
    timer = setTimeout(async () => {
      try { results = (await api(`users/lookup?q=${encodeURIComponent(q)}`)).items; active = results.length ? 0 : -1; render(); }
      catch (err) { if (err.status === 429) toast("error", "Slow down", "Too many searches. Wait a moment."); }
    }, 200);
  });
  input.addEventListener("keydown", (e) => {
    if (!list.classList.contains("open")) return;
    if (e.key === "ArrowDown") { active = Math.min(results.length - 1, active + 1); render(); e.preventDefault(); }
    if (e.key === "ArrowUp") { active = Math.max(0, active - 1); render(); e.preventDefault(); }
    if (e.key === "Enter" && active >= 0 && results[active]) { e.preventDefault(); choose(results[active]); }
    if (e.key === "Escape") close();
  });
  input.addEventListener("blur", () => setTimeout(close, 120));
  form.addEventListener("click", (e) => {
    if (!e.target.closest("[data-recipient-clear]")) return;
    pickedBox.hidden = true;
    search.hidden = false;
    input.value = "";
    form.dataset.recipientLabel = "";
    input.focus();
  });
  if (!pickedBox.hidden && pickedBox.querySelector("strong")) {
    form.dataset.recipientLabel = `${pickedBox.querySelector("strong")?.textContent} (${pickedBox.querySelector("small")?.textContent})`;
  } else if (input.value) {
    input.dispatchEvent(new Event("input"));
  }
  if (!search.hidden) input.focus();
}
