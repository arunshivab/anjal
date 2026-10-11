namespace Anjal.Webmail.Tests;

/// <summary>
/// Tests that make someone an operator do it through ANJAL_OPERATORS, which every test in this
/// assembly reads. Run alone, never beside other tests: found 8 Oct 2026 on the owner's laptop,
/// where the language page of an ordinary person showed the operators' language preview while an
/// operator test was running at the same time.
/// </summary>
[CollectionDefinition("Operators", DisableParallelization = true)]
public sealed class OperatorsRunAlone
{
}
