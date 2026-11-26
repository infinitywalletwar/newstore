// One PostgreSQL and one Kafka container for every test class in this assembly; each fixture gets its own database and its own
// consumer group.
[assembly: AssemblyFixture(typeof(NewStore.TestSupport.PostgresServer))]
[assembly: AssemblyFixture(typeof(NewStore.TestSupport.KafkaServer))]
