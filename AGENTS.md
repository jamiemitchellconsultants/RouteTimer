# Agent instructions

## Narrative contract

- `Narrative.md` is generated and never hand-edited; edit the fragment and recompile.
- A decision-bearing pull request needs **both** the `narrative-required` label **and** three body headings, spelled exactly as the template spells them:
  - `## Narrative Context`
  - `## Narrative Decision`
  - `## Narrative Consequences`
- The maintenance workflow fires on the **merge event only**. A missing label makes it exit silently; missing sections with the label present make it fail visibly. **Neither is repairable after merge** — labelling a merged pull request does nothing, and a missed entry has to be written by hand as a fragment.
- Supplying a pull-request body replaces the repository template wholesale. If you pass a body, carry the three sections in it yourself.
- A narrative-only pull request carries no label, or it would recursively generate an entry about maintaining the narrative.
- An accepted entry is never rewritten to read as though a later, better framing had been there all along. A reversal is a new entry of kind `correction` citing the original by slug — otherwise the record loses the evidence that the framing ever needed correcting.
