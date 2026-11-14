// One Floci, one PostgreSQL and one Kafka container for every test class in this assembly. Each test class makes its own bucket
// (Floci) and its own database (PostgreSQL).
[assembly: AssemblyFixture(typeof(NewStore.Media.Tests.S3Server))]
[assembly: AssemblyFixture(typeof(NewStore.TestSupport.PostgresServer))]
[assembly: AssemblyFixture(typeof(NewStore.TestSupport.KafkaServer))]
