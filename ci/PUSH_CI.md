# GitHub push builds

Pushes to `development` and manual runs build CI packages. Renderer pushes call these
workflows with their exact renderer SHA. Runtime and renderer revisions are resolved on
GitHub and pinned for the run. Packages, checksums and manifests are GitHub Actions
artifacts, retained for seven days in the calling workflow run. There are no scheduled jobs.

Pull request compile checks use read-only repository access, receive no package secrets,
and do not publish packages.

Packaging still downloads the SHA-256-pinned legacy base ZIP from GitLab as a binary input.
