# 0056: Tracked coding pull request rebases

- Status: Accepted for implementation. Live Railway and GitHub acceptance pending.
- Recorded: 2026-11-22
- Decision date: 2026-11-22
- Evidence: The owner asked the coding agent to support resolving merge conflicts on its pull requests. Existing CI and review follow-ups pin the PR head and publish a fast-forward commit.

## Context

When a tracked PR's base advances, adding a commit to the old branch can leave its conflict with the base unresolved. The coding agent needs to resolve and validate a new linear history while retaining the existing PR, application ownership and maintainer review.

## Decision

An explicit owner rebase follow-up pins the tracked head and protected base. The credential-free Railway workload replays the linear commits in order and exposes real Git conflict state to the coding agent. The trusted export records each original commit and its resolved file delta together with the tested final change.

The publisher reconstructs the commits in a disposable bare repository using the original messages and authors. It compares the resulting tree with the tested change, records the resulting commit before pushing, and uses an explicit Git expected-head lease to update the tracked PR branch. It rechecks PR ownership and base protection through the existing GitHub App flow. CI and review follow-ups retain their fast-forward publication path. See [Git push](https://git-scm.com/docs/git-push) and [GitHub App permissions for Git access](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/choosing-permissions-for-a-github-app#choosing-permissions-for-git-access).

## Alternatives

GitHub's REST forced ref update lacks an expected-head condition. A merge commit would change the agreed linear history. Opening another PR would separate the branch from its existing review.

## Consequences

The API publisher needs Git, and rebase jobs need a current coding checkpoint. The existing repository and artifact bounds continue to apply. A timeout with unresolved conflicts requires a fresh attempt. A complete replay remains eligible for the existing sandbox handoff.

## Delivery and verification

The Git fixtures exercise conflict resolution, replay ordering, checkpoint restoration, and a rejected push after concurrent branch movement. For live acceptance, deploy the updated Worker and API, prepare the labeled image, and rebase a disposable tracked PR with a known conflict. Verify commit order, validation, branch identity and refusal after a concurrent update. See the [platform coding runbook](../runbooks/platform-coding.md).
