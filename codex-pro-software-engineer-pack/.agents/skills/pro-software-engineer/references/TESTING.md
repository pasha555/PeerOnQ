# Testing and release reference

Read when adding tests, fixing regressions, preparing a release, or claiming completion.

## Test strategy
Use the cheapest test that proves the changed behavior:
- unit test for pure logic;
- component test for UI behavior;
- service/repository test for boundary logic;
- integration test for multiple real components;
- end-to-end test only for critical user journeys or integration contracts.

Prefer deterministic tests. Do not hide races with arbitrary long sleeps.

For a bug fix, add a regression test when practical:
1. reproduce the failure;
2. make the test fail for the right reason;
3. fix the code;
4. verify the test passes.

## Verification order
1. formatter/syntax/type checks relevant to touched files;
2. targeted tests;
3. relevant integration/build;
4. full suite when warranted.

Do not convert failing tests to skipped/disabled tests merely to obtain green CI.

## Release gate
Before calling work release-ready, verify as applicable:
- clean build;
- relevant automated tests;
- migrations are safe and ordered;
- required environment variables/config documented;
- no debug/test-only behavior enabled;
- secrets absent from diff;
- error paths observable;
- rollback/compatibility considered for high-impact changes.

Report exactly which checks were not run and why.
