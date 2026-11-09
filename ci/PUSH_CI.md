# GitHub push builds

The workflows in this repository run on pushes to `development` and can be started manually.
Renderer pushes call these same workflows with their exact renderer SHA. Source and renderer
revisions are pinned once per run, then the controller verifies those revisions on GitHub before building. GitLab is used only for package checks and publication. There are no scheduled jobs.

Publication uses the repository secret `GITLAB_PACKAGE_TOKEN`, a project-scoped deploy token
with read/write package-registry access. It has no repository or API write permission.

## Pull request compile checks

Pull requests compile their GitHub merge revision directly: the Linux converter and tests,
Windows runtime DLLs, converter, wheel probe, and launcher. GT7 also compiles its compatibility
libraries. These checks use read-only repository access, receive no package secrets, and never
upload or publish packages. They use no package publishing credentials.
