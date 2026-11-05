/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Hosting.Security;

namespace WebCore.Hosting.Tests;

/// <summary>Scanner names count only as a path's first segment: deeper segments are an app's own values (a random game code, a slug), which may start like one by chance.</summary>
public class ProbePathsTests
{
    [Theory]
    [InlineData("/play/PMAX7K")]
    [InlineData("/tv/SOLR42")]
    [InlineData("/host/session/PMA234")]
    [InlineData("/venues/vendor-night")]
    [InlineData("/quizzes/administrators-choice")]
    public void AScannerNameDeeperInThePath_IsNotAProbe(string path) => Assert.False(ProbePaths.IsProbe(path));

    [Theory]
    [InlineData("/pma/index")]
    [InlineData("/vendor/phpunit/phpunit/src/Util/PHP/eval-stdin")]
    [InlineData("/wp-admin/setup")]
    [InlineData("/phpmyadmin")]
    [InlineData("/play/.git/config")]
    [InlineData("/assets/old.php")]
    public void AScannerNameFirst_ADotSegmentAnywhere_OrAScriptExtension_IsAProbe(string path) => Assert.True(ProbePaths.IsProbe(path));
}
