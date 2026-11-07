// One PostgreSQL and one Kafka container for every test class in this assembly. Each test gets its own
// database and its own topics from them.
[assembly: AssemblyFixture(typeof(NewStore.TestSupport.PostgresServer))]
[assembly: AssemblyFixture(typeof(NewStore.TestSupport.KafkaServer))]
