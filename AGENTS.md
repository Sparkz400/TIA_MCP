# Agent instructions

## Keep the wiki current

`docs/wiki/` is the source of the GitHub wiki; `.github/workflows/wiki.yml`
publishes it on every push to `main` that changes `docs/wiki/**` (or on manual dispatch)
and overwrites edits made on the wiki itself.
Never edit the wiki directly.

In the same change, update the affected `docs/wiki/` page whenever you change:

- prerequisites, supported TIA Portal / PLCSIM versions or setup steps (`Getting-Started.md`, `FAQ.md`)
- build, packaging, launch commands or Host options (`Getting-Started.md`)
- components, worker boundaries or the change workflow (`Architecture.md`)
- the project status or safety statement in `README.md` (`Home.md`, `FAQ.md`)
- a docs page the wiki links to: its path or anchor (all pages)

Rules for wiki pages:

- Keep them an overview. Exact support claims, evidence and detail stay in `docs/`;
  link there instead of copying.
- Link repository files with absolute `https://github.com/Sparkz400/TIA_MCP/blob/main/...`
  URLs; relative links do not resolve on the wiki. Link wiki pages as `[[Page Name]]`.
- File names are page titles with hyphens (`Getting-Started.md` → "Getting Started").
  List new pages in `_Sidebar.md` and `Home.md`.

## Record Openness gaps

`docs/reference/openness-gaps.md` collects capabilities that the TIA Portal editor
has and the public Openness API lacks. Its open entries are sent to Siemens as
feature requests.

When work shows that something cannot be done because the public API does not
expose it, add an entry in the same change, using the template in that file:

- Check first that the gap is real: the public metadata of the installed assembly,
  a native call with its exact result, or the Siemens documentation. Link that
  evidence. Something this project has merely not implemented yet belongs in
  `docs/TODO.md`, not in the register.
- Search the register before adding; extend an existing entry with new evidence
  or a new affected version rather than adding a duplicate.
- Use the next free `G-NNN` number and never renumber or delete entries; change
  the status and give the reason.
- No customer project names, paths or content. Describe the reproduction on an
  owned fixture.
- A workaround through non-public API must be named as such in the entry.

Do not send anything to Siemens yourself; maintainers decide what is submitted.
