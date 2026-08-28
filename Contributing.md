# Contributing to RouteTimer

RouteTimer is developed in the open. To contribute, fork the repository, create a branch, make
and test your change, then open a pull request:

```bash
gh repo fork jamiemitchellconsultants/RouteTimer --clone
cd RouteTimer
git checkout -b your-branch-name
# make and test your change
git push -u origin your-branch-name
gh pr create
```

## Before opening a pull request

- Keep changes focused and update documentation when behavior or setup changes.
- Run the tests relevant to your change. The full .NET suite can be run with
  `dotnet test RouteTimer.slnx`.
- For client-side JavaScript changes, run `npm test` from `src/RouteTimer.Client`.
- Do not commit credentials, API keys, ride data, or other private information.
- Review the pull-request template and include all required information.

Pull requests need an approving review from a repository owner. The owners are listed in
[`.github/CODEOWNERS`](.github/CODEOWNERS).

## The project Narrative

`Narrative.md` is the repository's generated decision record. It preserves what was asked, what
was decided, why the decision was made, and what followed. The source material lives in
[`narrative/entries/`](narrative/entries/), with shared introductory text in
[`narrative/preamble.md`](narrative/preamble.md); [`Narrative.md`](Narrative.md) is compiled from
those files and must never be edited by hand.

Most mechanical changes do not need a narrative entry. A pull request that makes a meaningful
product, architecture, governance, operational, correction, or experimental decision must:

1. Apply the `narrative-required` label.
2. Include these headings in the pull-request body, spelled exactly:
   - `## Narrative Context`
   - `## Narrative Decision`
   - `## Narrative Consequences`

Explain the constraints and evidence in **Context**, the chosen approach and important rejected
alternatives in **Decision**, and the resulting trade-offs and open questions in **Consequences**.
The merge-time workflow uses those sections to create the entry, so the label and headings must be
present before the pull request is merged. The narrative validation workflow checks that fragments
and the generated `Narrative.md` agree.

If a change only updates the narrative itself, edit or add the appropriate fragment, regenerate
`Narrative.md`, and do not apply `narrative-required`; otherwise the maintenance workflow would
create a recursive entry about maintaining the narrative. Accepted entries are append-only: if a
later decision reverses an earlier one, add a new `correction` entry that cites the original by slug
instead of rewriting history.
