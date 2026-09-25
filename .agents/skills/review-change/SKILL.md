---
name: review-change
description: "Review a branch or diff against the spec or plan that asked for it, on three separate axes: Spec (does it do what was asked, and nothing else?), Correctness (bugs, regressions, missing tests) and Standards (the repo's documented rules plus a code-smell baseline). Use for the cross-model review of another agent's implementation. Invoke with an optional base ref and optional spec/plan paths."
argument-hint: "[base-ref] [spec-or-plan paths…]"
disable-model-invocation: true
---

# Review change

Review the diff between a fixed base and the current work on three axes, kept deliberately separate so a pass on one never masks a fail on another:

- **Spec**: does the change deliver what the originating spec/plan asked for, and only that?
- **Correctness**: is it right? Bugs, regressions, unsafe edge cases, missing or tautological tests.
- **Standards**: does it follow this repo's documented rules?

You are the reviewer, usually a different model from the one that wrote the code. Treat every claim the implementer made (commit messages, PR body, handoff notes, "all tests pass") as a claim to verify, not a fact.

This is a read-only review: do not edit files, commit, push, or post PR comments unless the user asks.

## Arguments

Treat any text after the invocation as arguments. A token that resolves with `git rev-parse --verify` is the base ref. A path that exists is a spec, plan, or brief. Anything else is focus guidance from the user.

## 1. Pin the diff

- Base: the argument if given; otherwise the default branch (`refs/remotes/origin/HEAD`, else `origin/master` / `origin/main`).
- Compute `MB=$(git merge-base <base> HEAD)`. The diff is `git diff $MB` (committed and uncommitted tracked changes). Also list untracked files from `git status --porcelain` and read any that belong to the change.
- Commit list: `git log --oneline $MB..HEAD`.
- Fail here, not later: a base that does not resolve or an empty diff ends the review with that message.
- If a PR exists for the branch (`gh pr view --json number,title,body,url`), read its body. Its Evidence and Merge-danger claims are inputs to verify.

## 2. Find the intent

Look for the originating spec and plan, in this order, and use every one that applies:

1. Paths passed as arguments, or a handoff/review brief the user pasted.
2. Spec/plan files the diff itself touches or that commit messages and the PR body name (e.g. `Spec 033`, `docs/plan/2026-09-24-…`).
3. Issue references in commits or the PR body (`#123`, `Closes #45`), read with `gh issue view`.
4. Repo spec/plan folders (for example `docs/spec/NNN-*/spec.md`, `docs/plan/*.md`) whose name matches the branch or the change.

Read the whole spec, including dated addenda: a later addendum overrides earlier text. A plan's `*-review.md` corrections override the plan. If you find nothing, ask the user once; if there is none, the Spec axis reports "no spec available" and the other two axes still run.

## 3. Find the standards

Read the repo's agent and contributor instructions (`AGENTS.md`, `CLAUDE.md`, `CONTRIBUTING.md`, and docs they point to that govern the changed area, such as versioning or API-compatibility rules). Keep only rules that apply to the changed files. Skip anything a compiler, analyzer, formatter or CI check already enforces, unless the diff suppresses or bypasses that check.

The Standards axis also carries this **smell baseline** (Fowler, _Refactoring_, ch. 3). A documented repo rule always overrides it, and each smell is a judgement call ("possible Feature Envy"), never a hard violation:

- **Mysterious Name**: the name doesn't reveal what it does or holds. → rename.
- **Duplicated Code**: the same logic shape in more than one hunk or file. → extract once, call twice.
- **Feature Envy**: a method uses another object's data more than its own. → move it to that data.
- **Data Clumps**: the same fields/params keep travelling together. → bundle them into one type.
- **Primitive Obsession**: a primitive or string standing in for a domain concept. → give it a type.
- **Repeated Switches**: the same switch/if-cascade on the same type recurs. → polymorphism or one shared map.
- **Shotgun Surgery**: one logical change forces scattered edits. → gather what changes together.
- **Divergent Change**: one module edited for several unrelated reasons. → split by reason.
- **Speculative Generality**: abstraction, parameters or hooks the spec doesn't need. → inline it back.
- **Message Chains**: long `a.b().c().d()` navigation. → hide the walk behind one method.
- **Middle Man**: a type or function that mostly delegates. → call the real target.
- **Refused Bequest**: an implementer that ignores most of what it inherits. → compose instead.

## 4. Verify

Read beyond the hunks where a finding depends on it: callers of a changed signature, other implementations of a changed interface, the tests that should cover the change. Confirm a finding against the code before reporting it.

Where the environment allows, run the targeted tests or build for the changed projects. If you cannot (read-only sandbox, missing services, too slow), say so in the report and do not state that tests pass.

## 5. Review each axis on its own

Work one axis at a time, with that axis's source open, and file each finding under exactly one axis.

- **Spec**, against the spec/plan: (a) requirements or acceptance criteria missing or only partly done; (b) behaviour the spec did not ask for (scope creep), including a non-goal that was built; (c) requirements that look implemented but behave differently from what the spec says. Quote the spec line for every finding.
- **Correctness**, against the code: bugs, broken edge cases, concurrency or ordering problems, error handling that swallows failures, security issues, breaking changes to public contracts, and changed behaviour with no test or with a test that cannot fail.
- **Standards**, against step 3: documented-rule breaches (cite the file and rule) and baseline smells (name the smell, quote the hunk).

Zero findings on an axis is a good result. Do not pad.

## 6. Report

Print the report in this shape. If you can write files, also save it to the OS temp directory as `review-<repo>-<branch>-<short-head-sha>.md` and print that path, so the implementing agent can read it directly.

```markdown
# Review: <branch> (<short base sha>..<short head sha>)

**Spec used:** <paths/issues, or "none found">
**Checks run by reviewer:** <commands and results, or "none: <why>">

## Spec
1. **[blocking|should-fix|nit]** <title> (`path:line`)
   > <quoted spec line>
   <what is wrong and what to change>

## Correctness
1. **[blocking|should-fix|nit]** <title> (`path:line`)
   <failure scenario: concrete input/state → wrong result; what to change>

## Standards
1. **[blocking|should-fix|nit]** <title> (`path:line`): <rule cited, or "possible <Smell>"> <what to change>
   (a smell is never blocking; a documented-rule breach can be)

## Summary
Spec: <n> findings, worst: <title or "none">. Correctness: <n>, worst: <…>. Standards: <n>, worst: <…>.
**Verdict:** ready to ship | fix blocking findings first
```

Do not merge or re-rank findings across axes. The verdict is "fix blocking findings first" whenever any axis has a blocking finding.

---

Adapted from Matt Pocock's `code-review` skill (github.com/mattpocock/skills, MIT): two-axis Standards/Spec review and the smell baseline. Changes: one agent instead of parallel sub-agents, an added Correctness axis, and repo-agnostic spec discovery.
