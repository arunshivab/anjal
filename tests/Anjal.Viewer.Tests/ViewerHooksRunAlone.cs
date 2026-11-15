namespace Anjal.Viewer.Tests;

/// <summary>
/// The supervisor tests turn on the viewer's test-only jobs (ANJAL_VIEWER_TEST_HOOKS) for the children they start.
/// Run alone, never beside the other viewer tests.
/// </summary>
[CollectionDefinition("Viewer hooks", DisableParallelization = true)]
public sealed class ViewerHooksRunAlone
{
}
