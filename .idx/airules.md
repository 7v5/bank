# AI rules for Gemini in Firebase Studio

Before making any change, read `AGENTS.md` at the repository root and follow it exactly.
It has the run and test commands, the approved design rules (Claret palette, no motion unless the user acts,
what each color means, the still rosette, button wording) and how to share work with Claude Code through branches.

The most important rules, in short:
- Nothing animates on its own. No count-ups, page transitions or chart entrance animation.
- Use the color tokens in `bank/wwwroot/css/app.css`. Claret means "clickable"; red is only for errors and
  actions that can't be undone, and those buttons are outlined, never filled.
- Every button says what it does ("Pay $40.00", not "Submit"). Destructive actions use `data-confirm`.
- Write original copy. Never copy another company's slogan or branding.
- Work on a `gemini/<topic>` branch, and run `dotnet build` and `dotnet test` before finishing.
