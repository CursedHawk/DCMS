# ADR 0002: AwesomeAssertions instead of FluentAssertions

**Status:** accepted (2026-06-12) · **Phase:** 1

## Context

FluentAssertions 8+ moved to a paid license for commercial use.
AwesomeAssertions is the community fork (API-compatible, same namespaces,
Apache-2.0).

## Decision

Use `AwesomeAssertions` in all test projects. Test code reads exactly like
FluentAssertions (`value.Should().Be(...)`).
