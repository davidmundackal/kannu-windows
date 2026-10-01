# Kannu for Windows — ART Framework

For every coding task, show the ART Breakdown first, load the skills it names as the very next tool
call, and only then inspect or modify the codebase.

- **A — Act as**: the persona best suited to the task (default: the Senior Windows Desktop Architect in
  `AGENTS.md`)
- **R — Request**: the actual task, stated plainly
- **T — Terms**: real constraints only, or "none specified"

```
ART Breakdown

- Act as: <persona>
- Request: <one sentence>
- Terms: <constraints/output format, or "none specified">
- Relevant skills: <exact names from the session's skill listing, or "none">
```

Then call the `Skill` tool for each named skill before any other tool. Zero skills is valid; never
name a skill that is not in the listing.

Everything else lives in `AGENTS.md`, imported below. Keep this import as the last line.

@AGENTS.md
