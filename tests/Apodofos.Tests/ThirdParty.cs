// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Xunit.Sdk;

namespace Apodofos.Tests;

/// <summary>
/// Third-party Oracle sample reports (copyrighted by their owners) are git-ignored and never part of the repository, so a fresh
/// clone does not have them. Tests that assert facts about those real reports use these attributes and are reported as
/// skipped (with the reason) when the files are absent; on a maintainer machine that has them they run normally.
/// </summary>
public enum ThirdPartyNeed
{
    /// <summary>The two sample reports copied from the git-ignored EXAMPLES folder.</summary>
    ExamplesFolder,
    /// <summary>The git-ignored EXAMPLES-local corpus.</summary>
    LocalCorpus
}

internal static class ThirdPartyAvailability
{
    public static string? SkipReason(ThirdPartyNeed need) => need switch
    {
        ThirdPartyNeed.ExamplesFolder when !Samples.HasExamples =>
            "Third-party sample reports (EXAMPLES/*EBSCDB*.html and *DB11G*.html) are not in the repository; add them locally to run this test.",
        ThirdPartyNeed.LocalCorpus when LocalCorpusTests.CorpusFiles().Count == 0 =>
            "Local report corpus (git-ignored EXAMPLES-local/) is not present on this machine.",
        _ => null
    };
}

[AttributeUsage(AttributeTargets.Method)]
[XunitTestCaseDiscoverer("Xunit.Sdk.FactDiscoverer", "xunit.execution.{Platform}")]
public sealed class ThirdPartyFactAttribute : FactAttribute
{
    public ThirdPartyFactAttribute(ThirdPartyNeed need = ThirdPartyNeed.ExamplesFolder) => Skip ??= ThirdPartyAvailability.SkipReason(need);
}

[AttributeUsage(AttributeTargets.Method)]
[XunitTestCaseDiscoverer("Xunit.Sdk.TheoryDiscoverer", "xunit.execution.{Platform}")]
public sealed class ThirdPartyTheoryAttribute : TheoryAttribute
{
    public ThirdPartyTheoryAttribute(ThirdPartyNeed need = ThirdPartyNeed.ExamplesFolder) => Skip ??= ThirdPartyAvailability.SkipReason(need);
}
