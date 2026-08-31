---
date: 2026-08-31
slug: add-granular-climb-focus-and-adjustment-card-grid
title: "Add granular climb focus and adjustment card grid"
summary: "Expose an integer climb-focus scale from 0 through 5. Level 0 submits the existing proportional mode with no bias; levels 1 through 5 submit climb-focused mode with linear biases of 1.2, 1.4, 1.6, 1.8, and 2.0."
kind: product
status: accepted
sequence: 2026-08-31T13:07:27.000Z
evidence: "https://github.com/jamiemitchellconsultants/RouteTimer/pull/55; merge commit 9cc051593401f689661206660d6335e9f7b8e645"
---

## Context

The time-target editor exposed only a binary proportional/climb-focused choice before revealing a technical bias field. Riders need a simpler, more granular control, and the five adjustment editors need clearer visual boundaries so their controls and submit actions are easier to associate.

## Decision

Expose an integer climb-focus scale from 0 through 5. Level 0 submits the existing proportional mode with no bias; levels 1 through 5 submit climb-focused mode with linear biases of 1.2, 1.4, 1.6, 1.8, and 2.0. Keep the existing API and persisted strategy contract unchanged by performing this translation in the client. Render enabled adjustment types as labelled cards in a responsive two-column grid that collapses to one column on narrower screens.

## Consequences

Riders gain six deterministic levels ranging from proportional to the existing maximum climb focus without entering algorithm-specific values. Existing requests and stored adjustments remain compatible, and no migration is required. The UI deliberately abstracts the exact bias values; changing the scale later would require a new product decision because the displayed level now carries stable meaning.
