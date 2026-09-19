<!--
Release notes template. Copy the body below into notes.md, fill it in, then:

  git tag -a vX.Y.Z -m "NimBus vX.Y.Z"
  git push origin vX.Y.Z
  gh release create vX.Y.Z --verify-tag --title "NimBus vX.Y.Z" --notes-file notes.md

Title is exactly `NimBus vX.Y.Z`. Read the two previous releases first and match
them (`gh release view v<prev>`). Full guidance: docs/versioning.md#cutting-a-release.
Keep it about the length of v3.6.0; one bullet per user-visible change, not per commit.
Delete this comment.
-->

NimBus X.Y.Z adds/improves/fixes <one sentence: what this release is for>.

### Highlights

- Added <feature> — <one sentence, what it does for the user>.
- Fixed <symptom> — <one sentence, what was wrong>.
- Improved <area> — <one sentence>.

### Compatibility

<Every change a consumer must act on: new or changed public surface, removed pages or endpoints,
moved config defaults, renamed metrics. Or: "No schema migration, topology change or wire change.">

This release was validated with the full .NET Release build and test suite, <N> frontend tests,
the frontend production build, <and the Cosmos DB / SQL Server conformance suites run live>.

PR: https://github.com/akakaule/NimBus/pull/<N>
