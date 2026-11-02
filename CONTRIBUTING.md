# Contributing to EKC

Thanks for helping. Please read the [development guide](docs/development-guide.md) first.

1. Open an issue describing the pattern or problem, with a minimal code sample.
2. Add a failing test (fixture in `samples/`, assertion in `tests/Ekc.Tests`).
3. Implement, keeping every graph edge backed by evidence and an honest confidence.
4. `dotnet test --solution Ekc.slnx` must pass with no warnings; update docs.

By contributing you agree that your contributions are licensed under the [Apache-2.0 license](LICENSE).
