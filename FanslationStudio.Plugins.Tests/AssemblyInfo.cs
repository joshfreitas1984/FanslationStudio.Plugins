// The services under test keep static state, and YamlHelper shares one static SharpYaml
// Serializer that is not thread-safe. The game only touches them from the main thread, so run
// test classes sequentially rather than making production code thread-safe for tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
