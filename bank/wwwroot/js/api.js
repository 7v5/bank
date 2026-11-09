// Thin fetch wrapper for the app's own /api/v1 endpoints.
// Sends the antiforgery token (from the layout's <meta name="csrf-token">) on unsafe methods
// and turns RFC 7807 problem responses into thrown errors with a readable message.

export class ApiError extends Error {
  constructor(status, problem) {
    super(problem?.detail || problem?.title || `Request failed (${status})`);
    this.status = status;
    this.problem = problem;
  }
}

export async function api(path, { method = "GET", body, signal } = {}) {
  const headers = { Accept: "application/json" };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (method !== "GET") headers.RequestVerificationToken = document.querySelector('meta[name="csrf-token"]')?.content ?? "";
  const res = await fetch(`/api/v1/${path.replace(/^\//, "")}`, {
    method, headers, signal, credentials: "same-origin",
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  if (res.status === 204) return null;
  const isJson = res.headers.get("content-type")?.includes("json");
  const data = isJson ? await res.json() : null;
  if (!res.ok) throw new ApiError(res.status, data);
  return data;
}
