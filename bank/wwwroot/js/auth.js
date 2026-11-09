// Login & register niceties: demo-account quick fill, show/hide password, password strength meter.

document.querySelectorAll(".demo-login").forEach((b) =>
  b.addEventListener("click", () => {
    const email = document.querySelector("#Email");
    const pw = document.querySelector("#Password");
    email.value = b.dataset.email;
    pw.value = "Demo123!";
    document.querySelectorAll(".demo-login").forEach((x) => x.classList.toggle("picked", x === b));
    document.querySelector("form button[type=submit]")?.focus();
  })
);

document.querySelectorAll("[data-password-toggle]").forEach((b) =>
  b.addEventListener("click", () => {
    const input = b.parentElement.querySelector("input");
    const show = input.type === "password";
    input.type = show ? "text" : "password";
    b.setAttribute("aria-label", show ? "Hide password" : "Show password");
    b.querySelector("use").setAttribute("href", show ? "#i-eye-off" : "#i-eye");
  })
);

document.querySelectorAll("[data-strength]").forEach((input) => {
  const meter = document.getElementById(input.dataset.strength);
  input.addEventListener("input", () => {
    const v = input.value;
    let score = 0;
    if (v.length >= 8) score++;
    if (/[A-Z]/.test(v) && /[a-z]/.test(v)) score++;
    if (/\d/.test(v)) score++;
    if (v.length >= 12 || /[^A-Za-z0-9]/.test(v)) score++;
    meter.dataset.score = v ? score : 0;
  });
});
