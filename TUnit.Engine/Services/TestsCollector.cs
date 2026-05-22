using TUnit.Core;

namespace TUnit.Engine.Services;

internal class TestsCollector(string sessionId)
{
    public IEnumerable<TestMetadata> GetTests()
    {
        // Iterate non-destructively. Source-generated registrations are populated once at
        // module init and must stay observable across multiple test sessions (MTP server
        // mode sends many `testing/runTests` to one process). Draining the queue made the
        // second session see no tests.
        foreach (var testSource in Sources.TestSources)
        {
            foreach (var testMetadata in testSource.CollectTests(sessionId))
            {
                yield return testMetadata;
            }
        }
    }

    public IEnumerable<DynamicTest> GetDynamicTests()
    {
        foreach (var dynamicTestSource in Sources.DynamicTestSources)
        {
            foreach (var dynamicTest in dynamicTestSource.CollectDynamicTests(sessionId))
            {
                yield return dynamicTest;
            }
        }
    }
}