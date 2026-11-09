# Code signing with SignPath

WyreMonitor's releases are not signed yet. The release workflow is ready to sign them through
SignPath, for example with a free certificate from [SignPath Foundation](https://signpath.org),
which accepts open source projects once they have broad public visibility (a first application,
in November 2026, was declined for that reason; it can be made again later). The policy is in
the main [README](../../README.md#code-signing-policy); this page is the maintainer's setup.

## Files here

| File | SignPath artifact configuration | Signs |
|---|---|---|
| [`app.xml`](app.xml) | slug `app` | `WyreMonitor.exe` and `WyreMonitor.dll`, before they are packaged |
| [`installer.xml`](installer.xml) | slug `installer` | `WyreMonitor-Setup-<version>.exe` |

Both check that each file's product name is `WyreMonitor` and its product version is the
release's version, as SignPath Foundation requires.

## Setup, once there is a certificate

1. In SignPath, in the project (slug `WyreMonitor`):
   - add the trusted build system **GitHub.com** and link it to the project;
   - create the two artifact configurations above, with the slugs `app` and `installer`,
     pasting the files' contents;
   - check that the signing policy is `release-signing` (SignPath Foundation's default).
2. Create a SignPath API token for a user with *submitter* rights on the project.
3. In GitHub, Settings > Secrets and variables > Actions:
   - secret `SIGNPATH_API_TOKEN`: the token;
   - variable `SIGNPATH_ORGANIZATION_ID`: the organization ID shown in SignPath;
   - only if they differ from the defaults: variables `SIGNPATH_PROJECT_SLUG` (default
     `WyreMonitor`) and `SIGNPATH_POLICY_SLUG` (default `release-signing`).
4. Try it without publishing: Actions > Release > Run workflow, tick **Sign**. Approve the two
   signing requests on SignPath (the app first, then the installer). The `installer` artifact
   of the run is the signed installer.

From then on, every version tag pushed by `Publish-Release.ps1` builds, waits for the two
approvals (up to four hours each), and publishes the signed installer. When the first signed
release is out, remove the "not code-signed yet" step from the README's *Install* section.
