# Versioning & Release Policy

NimBus packages (`Akaule.NimBus.*`) follow [SemVer 2.0](https://semver.org/):

- **Major** — breaking changes: removed/changed public API, or behavior changes a
  consumer could observe without a compile error (called out explicitly in the
  release notes under ⚠️ Breaking).
- **Minor** — new features and non-breaking additions. Deprecations land here:
  the old surface gets `[Obsolete]` with a message naming the replacement.
- **Patch** — fixes only.

**Obsolete lifecycle:** deprecate in a minor, delete in the *next* major. Bridge
code (adapter constructors, forwarding overloads) lives exactly one major cycle.

**Release mechanics:** releases are tag-driven. Pushing a `v*` tag runs
`.github/workflows/nuget-publish.yml`, which builds Release (warnings-as-errors
for compiler warnings), runs the full test suite, packs with `/p:Version` from
the tag, and pushes to nuget.org. A GitHub Release with categorized notes
(⚠️ Breaking / ✨ Features / 🔧 Improvements / 🐛 Fixes) accompanies every tag.

**Note for maintainers:** several DI registrations assume their service type has
a single public constructor (e.g. `AddSingleton<IManagerClient, ManagerClient>()`).
Adding a second constructor requires switching those registrations to explicit
factory lambdas to keep constructor selection deterministic.
