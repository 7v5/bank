// Notification bell: polls the API for the unread count, renders the dropdown on open,
// and pops a toast when something new arrives while the page is open.
import { api } from "./api.js";
import { toast } from "./ui.js";

const POLL_MS = 20000;

export function initNotifications() {
  const bell = document.querySelector("[data-notif-bell]");
  if (!bell) return;
  const panel = document.getElementById(bell.dataset.popover);
  const list = panel.querySelector(".notif-list");
  let lastLatestId = Number(bell.dataset.latest || 0);

  const setBadges = (count) => {
    document.querySelectorAll("[data-unread-badge]").forEach((b) => {
      b.textContent = count > 9 ? "9+" : String(count);
      b.hidden = count === 0;
    });
  };

  const render = (items) => {
    if (!items.length) {
      list.innerHTML = `<div class="empty" style="padding:32px 16px"><div class="empty-icon"><svg class="icon"><use href="#i-bell"/></svg></div><p>You're all caught up.</p></div>`;
      return;
    }
    list.innerHTML = "";
    for (const n of items) {
      const a = document.createElement("a");
      a.className = `notif${n.isRead ? "" : " unread"}`;
      a.href = n.link || "/Notifications";
      a.innerHTML = `<span class="tx-icon" style="--c:var(--primary)"><svg class="icon icon-sm"><use href="#i-${n.icon}"/></svg></span>
        <span><span class="notif-title"></span><span class="notif-body"></span><span class="notif-time"></span></span>`;
      a.querySelector(".notif-title").textContent = n.title;
      a.querySelector(".notif-body").textContent = n.body;
      a.querySelector(".notif-time").textContent = n.ago;
      a.addEventListener("click", () => { if (!n.isRead) api(`notifications/${n.id}/read`, { method: "POST" }).catch(() => {}); });
      list.append(a);
    }
  };

  panel.addEventListener("popover:open", async () => {
    list.innerHTML = `<div style="padding:12px;display:grid;gap:10px" aria-label="Loading">${'<div class="skeleton" style="height:52px"></div>'.repeat(3)}</div>`;
    try { render((await api("notifications?take=12")).items); } catch { list.innerHTML = '<p class="muted" style="padding:16px">Couldn\'t load notifications.</p>'; }
  });

  panel.querySelector("[data-mark-all]")?.addEventListener("click", async () => {
    await api("notifications/read-all", { method: "POST" });
    panel.querySelectorAll(".notif.unread").forEach((n) => n.classList.remove("unread"));
    setBadges(0);
  });

  const poll = async () => {
    if (document.hidden) return;
    try {
      const s = await api("notifications/summary");
      setBadges(s.unread);
      if (s.latest && s.latest.id > lastLatestId && lastLatestId !== 0) {
        toast("info", s.latest.title, s.latest.body);
        document.dispatchEvent(new CustomEvent("bank:refresh"));
      }
      if (s.latest) lastLatestId = Math.max(lastLatestId, s.latest.id);
      document.querySelectorAll("[data-pending-badge]").forEach((b) => { b.textContent = s.pendingRequests; b.hidden = s.pendingRequests === 0; });
    } catch { /* offline or signed out: try again next tick */ }
  };
  setInterval(poll, POLL_MS);
  document.addEventListener("visibilitychange", () => { if (!document.hidden) poll(); });
}
